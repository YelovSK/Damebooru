import {
  Component,
  inject,
  input,
  ChangeDetectionStrategy,
  signal,
  effect,
  DestroyRef,
  computed,
  viewChild,
  untracked,
  ElementRef,
} from "@angular/core";
import { CommonModule, DOCUMENT } from "@angular/common";
import { RouterLink, Router } from "@angular/router";
import {
  Subject,
  switchMap,
  catchError,
  of,
  map,
  combineLatest,
  tap,
} from "rxjs";
import {
  toObservable,
  toSignal,
  takeUntilDestroyed,
} from "@angular/core/rxjs-interop";

import { DamebooruService } from "@services/api/damebooru/damebooru.service";
import { SettingsService } from "@services/settings.service";
import { ToastService } from "@services/toast.service";
import { TagPipe } from "@shared/pipes/escape-tag.pipe";
import { escapeTagName, getMediaType } from "@shared/utils/utils";
import {
  DamebooruPostDto,
  DamebooruPostsAroundDto,
  DamebooruTagDto,
  DuplicateType,
  SimilarPost,
  PostAuditEntry,
  TagCategoryKind,
  AutoTagProvider,
  AutoTagScanStatus,
  AutoTagScanStepStatus,
  PostAutoTagCandidate,
  PostAutoTagProviderStatus,
  PostAutoTagStatus,
} from "@models";
import { ButtonComponent } from "@shared/components/button/button.component";
import { AutocompleteComponent } from "@shared/components/autocomplete/autocomplete.component";
import { ProgressiveImageComponent } from "@shared/components/progressive-image/progressive-image.component";
import { PostTagSourcesComponent } from '@shared/components/post-tag-sources/post-tag-sources.component';
import { ZoomPanContainerComponent } from "@shared/components/zoom-pan-container/zoom-pan-container.component";
import {
  SimpleTabsComponent,
  SimpleTabComponent,
} from "@shared/components/simple-tabs";
import { TooltipDirective } from "@shared/directives";
import { HotkeysService } from "@services/hotkeys.service";
import { AppLinks } from "@app/app.paths";
import { PostEditService, PostEditTag } from "./post-edit.service";
import { FileSizePipe } from "@shared/pipes/file-size.pipe";
import { FileNamePipe } from "@shared/pipes/file-name.pipe";

@Component({
  selector: "app-post-detail",
  imports: [
    CommonModule,
    RouterLink,
    TagPipe,
    ButtonComponent,
    AutocompleteComponent,
    ProgressiveImageComponent,
    PostTagSourcesComponent,
    ZoomPanContainerComponent,
    SimpleTabsComponent,
    SimpleTabComponent,
    TooltipDirective,
    FileSizePipe,
    FileNamePipe,
  ],
  providers: [PostEditService],
  templateUrl: "./post-detail.component.html",
  styleUrl: "./post-detail.component.css",
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PostDetailComponent {
  private readonly damebooru = inject(DamebooruService);
  private readonly router = inject(Router);
  private readonly hotkeys = inject(HotkeysService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly document = inject(DOCUMENT);
  private readonly settingsService = inject(SettingsService);
  private readonly toastService = inject(ToastService);
  readonly editService = inject(PostEditService);

  readonly appLinks = AppLinks;
  readonly duplicateType = DuplicateType;

  // Video settings from user preferences
  readonly autoPlayVideos = this.settingsService.autoPlayVideos;
  readonly startVideosMuted = this.settingsService.startVideosMuted;

  id = input.required<string>();
  query = input<string | null>("");
  explorerLibraryId = input<string | null>(null);
  explorerPath = input<string | null>(null);

  readonly activeExplorerLibraryId = computed(() =>
    this.parsePositiveInt(this.explorerLibraryId()),
  );
  readonly activeExplorerPath = computed(() =>
    this.normalizeExplorerPath(this.explorerPath()),
  );
  readonly detailQueryParams = computed(() => ({
    query: this.query() || null,
    explorerLibraryId: this.activeExplorerLibraryId() ?? null,
    explorerPath: this.activeExplorerPath() || null,
  }));

  // Sidebar collapsed state
  sidebarCollapsed = signal(false);
  mobileImageViewerOpen = signal(false);
  postFilesExpanded = signal(false);

  private readonly zoomPan = viewChild<ZoomPanContainerComponent>("zoomPan");
  private readonly mediaContainer = viewChild<ElementRef<HTMLElement>>("mediaContainer");

  private swipePointerId: number | null = null;
  private swipeStartX: number | null = null;
  private swipeStartY: number | null = null;
  private swipeStartTime = 0;
  private readonly swipeMinDistancePx = 60;
  private readonly swipeMaxDurationMs = 700;
  private readonly swipeDirectionRatio = 1.3;
  private readonly tapMaxDistancePx = 14;
  private readonly tapMaxDurationMs = 500;

  // Triggers a local post stream refresh after in-place edits.
  private refreshTrigger = signal(0);
  private readonly postCache = signal(new Map<number, DamebooruPostDto>());

  isAutoTagging = signal(false);
  isAutoTagStatusLoading = signal(false);
  autoTagStatus = signal<PostAutoTagStatus | null>(null);

  // Tag autocomplete for edit mode
  private tagQuery$ = new Subject<string>();
  tagSuggestions = toSignal(
    this.tagQuery$.pipe(
      switchMap((word) => {
        if (word.length < 1) return of([]);
        return this.damebooru.getTags(`*${word}* sort:usages`, 0, 10).pipe(
          map((res) => res.results),
          catchError(() => of([])),
        );
      }),
      takeUntilDestroyed(this.destroyRef),
    ),
    { initialValue: [] as DamebooruTagDto[] },
  );
  tagSearchValue = signal("");

  // Sources edit value
  sourcesValue = signal("");

  post = toSignal(
    combineLatest([
      toObservable(this.id),
      toObservable(this.refreshTrigger),
    ]).pipe(
      switchMap(([id]) =>
        this.getPostWithCache(Number(id)).pipe(
          // Ensure error doesn't break the component stream
          catchError((err) => {
            console.error("Error fetching post detail:", err);
            return of(null);
          }),
        ),
      ),
    ),
  );

  // Pre-fetch surrounding posts
  around = toSignal(
    combineLatest([
      toObservable(this.id),
      toObservable(this.query),
      toObservable(this.explorerLibraryId),
      toObservable(this.explorerPath),
    ]).pipe(
      switchMap(([id, query, explorerLibraryId, explorerPath]) => {
        const parsedExplorerLibraryId = this.parsePositiveInt(explorerLibraryId);
        const normalizedExplorerPath = this.normalizeExplorerPath(explorerPath);

        const aroundRequest = parsedExplorerLibraryId !== null
          ? this.damebooru.getLibraryPostsAround(
              parsedExplorerLibraryId,
              Number(id),
              normalizedExplorerPath,
            )
          : this.damebooru.getPostsAround(Number(id), query ?? "");

        return aroundRequest.pipe(
          tap((around) => {
            if (around.prev) this.setCachedPost(around.prev);
            if (around.next) this.setCachedPost(around.next);
          }),
          catchError((err) => {
            console.error(
              "Around API failed (disabling keyboard nav for this post):",
              err,
            );
            return of({ prev: null, next: null } as DamebooruPostsAroundDto);
          }),
        );
      }),
    ),
  );

  similarPosts = computed<SimilarPost[]>(() => this.post()?.similarPosts ?? []);

  imageLoading = signal(true);
  auditEntries = signal<PostAuditEntry[]>([]);
  auditHasMore = signal(false);
  private auditRequestInFlight = false;

  constructor() {
    effect(() => {
      this.post();
      this.imageLoading.set(true);
      this.mobileImageViewerOpen.set(false);
      this.postFilesExpanded.set(false);
    });

    // Initialize sources value when entering edit mode
    effect(() => {
      const state = this.editService.currentState();
      if (state && this.sourcesValue() === "") {
        this.sourcesValue.set(state.sources.join("\n"));
      }
      if (!this.editService.isEditing()) {
        this.sourcesValue.set("");
        this.tagSearchValue.set("");
      }
    });

    this.setupHotkeys();

    effect(() => {
      const id = Number(this.id());
      if (!Number.isInteger(id) || id < 1) {
        this.auditEntries.set([]);
        this.auditHasMore.set(false);
        return;
      }

      untracked(() => this.loadAudit(id, false));
    });

    effect(() => {
      const id = Number(this.id());
      if (!Number.isInteger(id) || id < 1) {
        this.autoTagStatus.set(null);
        return;
      }

      untracked(() => this.loadAutoTagStatus(id));
    });
  }

  private getPostWithCache(id: number) {
    const cached = this.postCache().get(id);
    if (cached) {
      return of(cached);
    }

    return this.damebooru
      .getPost(id)
      .pipe(tap((post) => this.setCachedPost(post)));
  }

  private setCachedPost(post: DamebooruPostDto) {
    this.postCache.update((existing) => {
      const next = new Map(existing);
      next.set(post.id, post);
      return next;
    });
  }

  private setupHotkeys() {
    // Navigate Left
    this.hotkeys
      .on("ArrowLeft")
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        this.goToPrevPost();
      });

    // Navigate Right
    this.hotkeys
      .on("ArrowRight")
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        this.goToNextPost();
      });

    // Edit mode toggle
    this.hotkeys
      .on("e")
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        if (this.editService.isEditing()) {
          this.cancelEditing();
        } else {
          this.startEditing();
        }
      });

    this.hotkeys
      .on("f", { preventDefault: true })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        this.toggleFullscreen();
      });
  }

  toggleFullscreen(): void {
    if (this.editService.isEditing()) {
      return;
    }

    if (this.document.fullscreenElement) {
      void this.document.exitFullscreen();
      return;
    }

    const element = this.mediaContainer()?.nativeElement;
    if (element) {
      void element.requestFullscreen();
    }
  }

  getTagCategoryLabel(tag: { category?: TagCategoryKind | null }): string {
    switch (tag.category) {
      case TagCategoryKind.General:
        return 'General';
      case TagCategoryKind.Artist:
        return 'Artist';
      case TagCategoryKind.Character:
        return 'Character';
      case TagCategoryKind.Copyright:
        return 'Copyright';
      case TagCategoryKind.Meta:
        return 'Meta';
      default:
        return 'General';
    }
  }

  getTagCategoryColor(tag: { category?: TagCategoryKind | null }): string {
    switch (tag.category) {
      case TagCategoryKind.General:
        return '#7dd3fc';
      case TagCategoryKind.Artist:
        return '#f9a8d4';
      case TagCategoryKind.Character:
        return '#86efac';
      case TagCategoryKind.Copyright:
        return '#fcd34d';
      case TagCategoryKind.Meta:
        return '#c4b5fd';
      default:
        return 'var(--color-text-dim)';
    }
  }

  // Sidebar toggle
  toggleSidebar() {
    this.sidebarCollapsed.update((v) => !v);
  }

  togglePostFiles(): void {
    this.postFilesExpanded.update((v) => !v);
  }

  goToPrevPost() {
    this.navigateToPost(this.around()?.prev);
  }

  goToNextPost() {
    this.navigateToPost(this.around()?.next);
  }

  onMediaPointerDown(event: PointerEvent) {
    if (event.pointerType === "mouse") return;
    if (!event.isPrimary) return;

    this.swipePointerId = event.pointerId;
    this.swipeStartX = event.clientX;
    this.swipeStartY = event.clientY;
    this.swipeStartTime = performance.now();

    if (event.currentTarget instanceof HTMLElement) {
      event.currentTarget.setPointerCapture(event.pointerId);
    }
  }

  onMediaPointerUp(event: PointerEvent) {
    if (this.swipePointerId !== event.pointerId) return;
    if (this.swipeStartX === null || this.swipeStartY === null) {
      this.releaseMediaPointer(event);
      return;
    }

    const deltaX = event.clientX - this.swipeStartX;
    const deltaY = event.clientY - this.swipeStartY;
    const elapsedMs = performance.now() - this.swipeStartTime;
    this.releaseMediaPointer(event);
    this.resetSwipeState();

    if (this.isMobileImageViewerTap(deltaX, deltaY, elapsedMs)) {
      this.openMobileImageViewer();
      return;
    }

    if (elapsedMs > this.swipeMaxDurationMs) return;
    if (Math.abs(deltaX) < this.swipeMinDistancePx) return;
    if (Math.abs(deltaX) < Math.abs(deltaY) * this.swipeDirectionRatio) return;

    if (deltaX < 0) {
      this.goToNextPost();
      return;
    }

    this.goToPrevPost();
  }

  openMobileImageViewer() {
    const post = this.post();
    if (!post || this.editService.isEditing()) {
      return;
    }

    const mediaType = this.getMediaType(post.contentType);
    if ((mediaType === 'image' || mediaType === 'animation') && this.isMobileViewport()) {
      this.mobileImageViewerOpen.set(true);
    }
  }

  closeMobileImageViewer() {
    this.mobileImageViewerOpen.set(false);
  }

  onMediaPointerCancel(event?: PointerEvent) {
    if (event) {
      this.releaseMediaPointer(event);
    }
    this.resetSwipeState();
  }

  private releaseMediaPointer(event: PointerEvent) {
    if (
      event.currentTarget instanceof HTMLElement &&
      event.currentTarget.hasPointerCapture(event.pointerId)
    ) {
      event.currentTarget.releasePointerCapture(event.pointerId);
    }
  }

  private resetSwipeState() {
    this.swipePointerId = null;
    this.swipeStartX = null;
    this.swipeStartY = null;
    this.swipeStartTime = 0;
  }

  private isMobileImageViewerTap(deltaX: number, deltaY: number, elapsedMs: number): boolean {
    return elapsedMs <= this.tapMaxDurationMs
      && Math.hypot(deltaX, deltaY) <= this.tapMaxDistancePx;
  }

  private isMobileViewport(): boolean {
    return window.matchMedia('(max-width: 1100px)').matches;
  }

  // Edit mode methods
  startEditing() {
    const post = this.post();
    if (post) {
      this.editService.startEditing(post);
      this.sourcesValue.set((post.sources || []).join("\n"));
    }
  }

  cancelEditing() {
    this.editService.cancelEditing();
  }

  saveChanges() {
    this.editService.save(this.destroyRef).subscribe((updatedPost) => {
      if (updatedPost) {
        this.setCachedPost(updatedPost);
        this.refreshTrigger.update((n) => n + 1);
        this.reloadAuditForCurrentPost();
      }
    });
  }

  // Sources
  onSourcesChange(event: Event) {
    const value = (event.target as HTMLTextAreaElement).value;
    this.sourcesValue.set(value);
    const sources = value
      .split("\n")
      .map((s) => s.trim())
      .filter((s) => s.length > 0);
    this.editService.setSources(sources);
  }

  // Tags
  onTagQueryChange(word: string) {
    this.tagQuery$.next(escapeTagName(word));
  }

  onTagSelection(tag: DamebooruTagDto) {
    this.editService.addTag(tag.name, tag.id, tag.category);
    this.tagSearchValue.set("");
    this.tagQuery$.next("");
  }

  onTagSearch(value: string) {
    // On enter, add the tag directly
    const trimmed = value.trim();
    if (trimmed) {
      this.editService.addTag(trimmed);
      this.tagSearchValue.set("");
      this.tagQuery$.next("");
    }
  }

  removeTag(tag: PostEditTag) {
    this.editService.removeTag(tag);
  }

  protected trackByEditTag(tag: PostEditTag): string {
    return `${tag.sources.join(',')}|${tag.name}`;
  }

  autoTagPost() {
    const post = this.post();
    if (!post || this.isAutoTagging()) return;

    this.isAutoTagging.set(true);
    this.damebooru.autoTagPost(post.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: result => {
          this.setCachedPost(result.post);
          this.refreshTrigger.update((n) => n + 1);
          this.reloadAuditForCurrentPost();
          this.loadAutoTagStatus(result.post.id);

          const parts: string[] = [];
          if (result.addedTags > 0) parts.push(`added ${result.addedTags} tag${result.addedTags === 1 ? '' : 's'}`);
          if (result.removedTags > 0) parts.push(`removed ${result.removedTags} tag${result.removedTags === 1 ? '' : 's'}`);
          if (result.updatedTagCategories > 0) parts.push(`updated ${result.updatedTagCategories} categor${result.updatedTagCategories === 1 ? 'y' : 'ies'}`);
          if (result.addedSources > 0) parts.push(`added ${result.addedSources} source${result.addedSources === 1 ? '' : 's'}`);

          this.toastService.success(parts.length > 0 ? `Auto-tagging complete: ${parts.join(', ')}` : 'Auto-tagging complete with no changes');
          this.isAutoTagging.set(false);
        },
        error: (error) => {
          this.toastService.error(error?.error || 'Auto-tagging failed');
          this.isAutoTagging.set(false);
        },
      });
  }

  toggleFavorite() {
    const post = this.post();
    if (!post) return;

    const operation = post.isFavorite
      ? this.damebooru.unfavoritePost(post.id)
      : this.damebooru.favoritePost(post.id);

    operation
      .pipe(switchMap(() => this.damebooru.getPost(post.id)))
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (updatedPost) => {
          this.setCachedPost(updatedPost);
          this.refreshTrigger.update((n) => n + 1);
          this.reloadAuditForCurrentPost();
          this.toastService.success(
            updatedPost.isFavorite ? "Post favorited" : "Post unfavorited",
          );
        },
        error: () => {
          this.toastService.error("Failed to update favorite");
        },
      });
  }

  getMediaType(contentType: string) {
    return getMediaType(contentType);
  }

  getThumbnailUrl(post: DamebooruPostDto): string {
    return this.damebooru.getThumbnailUrl(
      post.thumbnailLibraryId,
      post.thumbnailContentHash,
    );
  }

  getPostContentUrl(post: DamebooruPostDto): string {
    return this.damebooru.getPostContentUrl(post.id);
  }

  getSimilarPostThumbnailUrl(post: SimilarPost): string {
    return this.damebooru.getThumbnailUrl(
      post.thumbnailLibraryId,
      post.thumbnailContentHash,
    );
  }

  private navigateToPost(post: DamebooruPostDto | null | undefined) {
    if (!post) return;

    if (this.editService.isEditing()) {
      this.editService.cancelEditing();
    }

    this.zoomPan()?.resetZoom();
    this.closeMobileImageViewer();

    this.router.navigate(AppLinks.post(post.id), {
      queryParams: this.detailQueryParams(),
      replaceUrl: true,
    });
  }

  loadMoreAudit() {
    const id = Number(this.id());
    if (!Number.isInteger(id) || id < 1) {
      return;
    }

    this.loadAudit(id, true);
  }

  describeAuditEntry(entry: PostAuditEntry): string {
    const field = entry.field;
    const operation = entry.operation.toLowerCase();
    if (operation === "insert") {
      return `${field} added`;
    }

    if (operation === "delete") {
      return `${field} removed`;
    }

    return `${field} changed`;
  }

  getAutoTagStatusLabel(status: PostAutoTagStatus | null): string {
    if (!status || !status.hasScan || status.scanStatus === null) {
      return 'Not scanned';
    }

    switch (status.scanStatus) {
      case AutoTagScanStatus.Pending:
        return 'Pending';
      case AutoTagScanStatus.InProgress:
        return 'In progress';
      case AutoTagScanStatus.Partial:
        return 'Partial';
      case AutoTagScanStatus.Completed:
        return 'Completed';
      case AutoTagScanStatus.Failed:
        return 'Failed';
      default:
        return 'Unknown';
    }
  }

  getAutoTagStatusClass(status: PostAutoTagStatus | null): string {
    if (!status || !status.hasScan || status.scanStatus === null) {
      return 'bg-white/10 text-text-muted border-white/15';
    }

    switch (status.scanStatus) {
      case AutoTagScanStatus.Completed:
        return 'bg-status-success/15 text-status-success border-status-success/30';
      case AutoTagScanStatus.Partial:
        return 'bg-status-warning/15 text-status-warning border-status-warning/30';
      case AutoTagScanStatus.Failed:
        return 'bg-status-error/15 text-status-error border-status-error/30';
      case AutoTagScanStatus.InProgress:
        return 'bg-accent-primary/15 text-accent-primary border-accent-primary/30';
      default:
        return 'bg-white/10 text-text-muted border-white/15';
    }
  }

  getProviderLabel(provider: AutoTagProvider): string {
    switch (provider) {
      case AutoTagProvider.SauceNao:
        return 'SauceNAO';
      case AutoTagProvider.Iqdb:
        return 'IQDB';
      case AutoTagProvider.Danbooru:
        return 'Danbooru';
      case AutoTagProvider.Gelbooru:
        return 'Gelbooru';
      default:
        return 'Unknown';
    }
  }

  getProviderStatusLabel(providerStatus: PostAutoTagProviderStatus): string {
    if (!providerStatus.isEnabled) {
      return 'Disabled';
    }

    switch (providerStatus.status) {
      case AutoTagScanStepStatus.Succeeded:
        return providerStatus.tagCount > 0 || providerStatus.sourceCount > 0 || providerStatus.externalPostId !== null
          ? 'Succeeded'
          : 'No match';
      case AutoTagScanStepStatus.Skipped:
        return 'Skipped';
      case AutoTagScanStepStatus.RetryableFailure:
        return 'Retry later';
      case AutoTagScanStepStatus.PermanentFailure:
        return 'Failed';
      case AutoTagScanStepStatus.Running:
        return 'Running';
      case AutoTagScanStepStatus.Pending:
        return 'Pending';
      default:
        return 'Not run';
    }
  }

  getProviderStatusClass(providerStatus: PostAutoTagProviderStatus): string {
    if (!providerStatus.isEnabled) {
      return 'text-text-muted';
    }

    switch (providerStatus.status) {
      case AutoTagScanStepStatus.Succeeded:
        return 'text-status-success';
      case AutoTagScanStepStatus.RetryableFailure:
      case AutoTagScanStepStatus.PermanentFailure:
        return 'text-status-error';
      case AutoTagScanStepStatus.Running:
        return 'text-accent-primary';
      default:
        return 'text-text-muted';
    }
  }

  getDiscoveryCandidates(provider: AutoTagProvider): PostAutoTagCandidate[] {
    return this.autoTagStatus()?.candidates.filter(candidate => candidate.discoveryProvider === provider) ?? [];
  }

  private reloadAuditForCurrentPost() {
    const id = Number(this.id());
    if (!Number.isInteger(id) || id < 1) {
      return;
    }

    this.loadAudit(id, false);
  }

  private loadAudit(postId: number, append: boolean) {
    if (this.auditRequestInFlight) {
      return;
    }

    const currentEntries = this.auditEntries();
    const beforeId = append && currentEntries.length > 0
      ? currentEntries[currentEntries.length - 1].id
      : undefined;

    this.auditRequestInFlight = true;
    this.damebooru
      .getPostAudit(postId, beforeId, 50)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          if (append) {
            this.auditEntries.set([...currentEntries, ...result.items]);
          } else {
            this.auditEntries.set(result.items);
          }

          this.auditHasMore.set(result.hasMore);
          this.auditRequestInFlight = false;
        },
        error: () => {
          this.auditRequestInFlight = false;
        },
      });
  }

  private loadAutoTagStatus(postId: number) {
    this.isAutoTagStatusLoading.set(true);
    this.damebooru
      .getPostAutoTagStatus(postId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: status => {
          this.autoTagStatus.set(status);
          this.isAutoTagStatusLoading.set(false);
        },
        error: () => {
          this.autoTagStatus.set(null);
          this.isAutoTagStatusLoading.set(false);
        },
      });
  }

  private parsePositiveInt(value: string | null | undefined): number | null {
    if (!value) {
      return null;
    }

    const parsed = Number(value);
    if (!Number.isInteger(parsed) || parsed < 1) {
      return null;
    }

    return parsed;
  }

  private normalizeExplorerPath(value: string | null | undefined): string {
    if (!value) {
      return "";
    }

    const parts = value
      .replace(/\\/g, "/")
      .split("/")
      .map((part) => part.trim())
      .filter((part) => part.length > 0 && part !== "." && part !== "..");

    return parts.join("/");
  }

  protected trackByTag(tag: DamebooruTagDto): string {
    return tag.id.toString();
  }
}
