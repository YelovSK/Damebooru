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
  type ElementRef,
} from "@angular/core";
import { CommonModule, DOCUMENT } from "@angular/common";
import { RouterLink, Router } from "@angular/router";
import {
  Subject,
  fromEvent,
  switchMap,
  catchError,
  of,
  map,
} from "rxjs";
import {
  toSignal,
  takeUntilDestroyed,
} from "@angular/core/rxjs-interop";

import { DamebooruService } from "@services/api/damebooru/damebooru.service";
import { ImagePreloadService } from "@services/image-preload.service";
import { SettingsService } from "@services/settings.service";
import { ToastService } from "@services/toast.service";
import { TagPipe } from "@shared/pipes/escape-tag.pipe";
import { escapeTagName, getMediaType } from "@shared/utils/utils";
import {
  type DamebooruPostDto,
  type DamebooruTagDto,
  DuplicateType,
  type SimilarPost,
  type PostAuditEntry,
  TagCategoryKind,
  AutoTagProvider,
  AutoTagScanStatus,
  AutoTagScanStepStatus,
  type PostAutoTagCandidate,
  type PostAutoTagProviderStatus,
  type PostAutoTagStatus,
  type AiTagPreview,
} from "@models";
import { AutocompleteComponent } from "@shared/components/autocomplete/autocomplete.component";
import { ProgressiveImageComponent } from "@shared/components/progressive-image/progressive-image.component";
import { PostTagSourcesComponent } from '@shared/components/post-tag-sources/post-tag-sources.component';
import { ZoomPanContainerComponent } from "@shared/components/zoom-pan-container/zoom-pan-container.component";
import { TabComponent } from "@shared/components/tabs/tab.component";
import { TabsComponent } from "@shared/components/tabs/tabs.component";
import { ModalComponent } from "@shared/components/modal/modal.component";
import { ButtonDirective, TooltipDirective } from "@shared/directives";
import { HotkeysService } from "@services/hotkeys.service";
import { AppLinks } from "@app/app.paths";
import { PostEditService, type PostEditTag } from "./post-edit.service";
import { FileSizePipe } from "@shared/pipes/file-size.pipe";
import { FileNamePipe } from "@shared/pipes/file-name.pipe";

type NavigationFetchDirection = "initial" | "previous" | "next";

@Component({
  selector: "app-post-detail",
  imports: [
    CommonModule,
    RouterLink,
    TagPipe,
    AutocompleteComponent,
    ProgressiveImageComponent,
    PostTagSourcesComponent,
    ZoomPanContainerComponent,
    TabsComponent,
    TabComponent,
    ModalComponent,
    ButtonDirective,
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
  private readonly imagePreloadService = inject(ImagePreloadService);
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

  private readonly navigationPosts = signal<DamebooruPostDto[]>([]);
  private readonly navigationHasPrevious = signal(false);
  private readonly navigationHasNext = signal(false);
  private readonly initialBeforeCount = 25;
  private readonly initialAfterCount = 25;
  private readonly refillOverlapCount = 10;
  private readonly refillAheadCount = 50;
  private readonly refillThreshold = 10;
  private readonly keepBehindCount = 40;
  private readonly keepAheadCount = 70;
  private readonly previewPreloadBehindCount = 8;
  private readonly previewPreloadAheadCount = 20;
  private navigationScopeKey = "";
  private readonly navigationRequestsInFlight = new Set<string>();
  private pendingEdgeNavigation: NavigationFetchDirection | null = null;

  isAutoTagging = signal(false);
  isAiTagPreviewLoading = signal(false);
  aiTagPreview = signal<AiTagPreview | null>(null);
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

  readonly currentPostId = computed(() => {
    const id = Number(this.id());
    return Number.isInteger(id) && id > 0 ? id : null;
  });

  post = computed(() => {
    const id = this.currentPostId();
    if (id === null) {
      return null;
    }

    return this.navigationPosts().find((post) => post.id === id) ?? null;
  });

  private readonly currentWindowIndex = computed(() => {
    const id = this.currentPostId();
    if (id === null) {
      return -1;
    }

    return this.navigationPosts().findIndex((post) => post.id === id);
  });

  readonly around = computed(() => {
    const index = this.currentWindowIndex();
    if (index < 0) {
      return { prev: null, next: null };
    }

    const posts = this.navigationPosts();
    return {
      prev: posts[index - 1]?.id ?? (this.navigationHasPrevious() ? -1 : null),
      next: posts[index + 1]?.id ?? (this.navigationHasNext() ? -1 : null),
    };
  });

  similarPosts = computed<SimilarPost[]>(() => this.post()?.similarPosts ?? []);

  suppressFullImage = signal(false);
  imageLoading = signal(true);
  auditEntries = signal<PostAuditEntry[]>([]);
  auditHasMore = signal(false);
  private auditRequestInFlight = false;
  private auditRequestPostId: number | null = null;
  private activeSidebarTabId = "info";
  private auditLoadedForPostId: number | null = null;
  private autoTagStatusLoadedForPostId: number | null = null;
  private autoTagStatusRequestInFlight = false;
  private autoTagStatusRequestPostId: number | null = null;
  private lastResetPostId: number | null = null;
  private readonly heldNavigationKeys = new Set<string>();

  constructor() {
    effect(() => {
      const postId = this.currentPostId();
      if (postId === this.lastResetPostId) {
        return;
      }

      this.lastResetPostId = postId;
      this.imageLoading.set(true);
      this.mobileImageViewerOpen.set(false);
      this.postFilesExpanded.set(false);
      untracked(() => this.resetOnDemandTabData(postId));
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
      const id = this.currentPostId();
      if (id === null) {
        return;
      }

      const scopeKey = this.getNavigationScopeKey();
      this.navigationPosts();
      this.navigationHasPrevious();
      this.navigationHasNext();

      untracked(() => {
        this.ensureNavigationWindowForPost(id, scopeKey);
      });
    });

    effect(() => {
      const posts = this.navigationPosts();
      const currentIndex = this.currentWindowIndex();
      if (currentIndex < 0) {
        return;
      }

      untracked(() => this.preloadNearbyPreviews(posts, currentIndex));
    });
  }

  private resetNavigationScope(nextScopeKey: string) {
    this.navigationScopeKey = nextScopeKey;
    this.navigationPosts.set([]);
    this.navigationHasPrevious.set(false);
    this.navigationHasNext.set(false);
    this.navigationRequestsInFlight.clear();
    this.pendingEdgeNavigation = null;
  }

  private ensureNavigationWindowForPost(id: number, scopeKey: string) {
    if (scopeKey !== this.navigationScopeKey) {
      this.resetNavigationScope(scopeKey);
    }

    const currentIndex = this.navigationPosts().findIndex((post) => post.id === id);
    if (currentIndex < 0) {
      this.requestNavigationWindow(
        id,
        this.initialBeforeCount,
        this.initialAfterCount,
        "initial",
      );
      return;
    }

    this.prefetchNavigationEdges(id, currentIndex);
  }

  private prefetchNavigationEdges(id: number, currentIndex: number) {
    const posts = this.navigationPosts();
    const postsBefore = currentIndex;
    const postsAfter = posts.length - currentIndex - 1;

    if (this.navigationHasPrevious() && postsBefore <= this.refillThreshold) {
      this.requestNavigationWindow(
        id,
        this.refillAheadCount,
        this.refillOverlapCount,
        "previous",
      );
    }

    if (this.navigationHasNext() && postsAfter <= this.refillThreshold) {
      this.requestNavigationWindow(
        id,
        this.refillOverlapCount,
        this.refillAheadCount,
        "next",
      );
    }
  }

  private requestNavigationWindow(
    anchorId: number,
    before: number,
    after: number,
    direction: NavigationFetchDirection,
    navigateAfterLoad = false,
  ) {
    const scopeKey = this.navigationScopeKey || this.getNavigationScopeKey();
    const directionKey = direction === "initial"
      ? `${scopeKey}|${direction}|${anchorId}`
      : `${scopeKey}|${direction}`;
    if (this.navigationRequestsInFlight.has(directionKey)) {
      if (navigateAfterLoad) {
        this.pendingEdgeNavigation = direction;
      }
      return;
    }

    this.navigationRequestsInFlight.add(directionKey);
    if (navigateAfterLoad) {
      this.pendingEdgeNavigation = direction;
    }

    const parsedExplorerLibraryId = this.activeExplorerLibraryId();
    const normalizedExplorerPath = this.activeExplorerPath();
    const request = parsedExplorerLibraryId !== null
      ? this.damebooru.getLibraryPostsAround(
          parsedExplorerLibraryId,
          anchorId,
          normalizedExplorerPath,
          Math.max(before, after),
          before,
          after,
        )
      : this.damebooru.getPostsAround(
          anchorId,
          this.query() ?? "",
          Math.max(before, after),
          before,
          after,
        );

    request
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (around) => {
          this.navigationRequestsInFlight.delete(directionKey);
          if (scopeKey !== this.navigationScopeKey) {
            return;
          }

          this.mergeNavigationWindow(around.items ?? [], around.hasPrevious ?? false, around.hasNext ?? false, direction);
          this.completePendingEdgeNavigation(direction);
        },
        error: (err) => {
          console.error("Around API failed (keyboard nav may pause at the current edge):", err);
          this.navigationRequestsInFlight.delete(directionKey);
          if (this.pendingEdgeNavigation === direction) {
            this.pendingEdgeNavigation = null;
          }
        },
      });
  }

  private mergeNavigationWindow(
    incoming: DamebooruPostDto[],
    hasPrevious: boolean,
    hasNext: boolean,
    direction: NavigationFetchDirection,
  ) {
    if (incoming.length === 0) {
      return;
    }

    const existing = this.navigationPosts();
    const merged = this.mergeOrderedPosts(existing, incoming, direction);
    const currentId = this.currentPostId();
    const currentIndex = currentId === null
      ? -1
      : merged.findIndex((post) => post.id === currentId);

    const trimmed = currentIndex >= 0
      ? this.trimNavigationPosts(merged, currentIndex)
      : { posts: merged, start: 0, end: merged.length };

    let nextHasPrevious = direction === "next"
      ? this.navigationHasPrevious()
      : hasPrevious;
    let nextHasNext = direction === "previous"
      ? this.navigationHasNext()
      : hasNext;

    if (trimmed.start > 0) {
      nextHasPrevious = true;
    }
    if (trimmed.end < merged.length) {
      nextHasNext = true;
    }

    this.navigationPosts.set(trimmed.posts);
    this.navigationHasPrevious.set(nextHasPrevious);
    this.navigationHasNext.set(nextHasNext);
  }

  private mergeOrderedPosts(
    existing: DamebooruPostDto[],
    incoming: DamebooruPostDto[],
    direction: NavigationFetchDirection,
  ): DamebooruPostDto[] {
    if (existing.length === 0 || direction === "initial") {
      return this.dedupePosts(incoming);
    }

    const existingIndexById = new Map(existing.map((post, index) => [post.id, index]));
    let firstIncomingOverlap = -1;
    let firstExistingOverlap = -1;
    let lastIncomingOverlap = -1;
    let lastExistingOverlap = -1;

    for (let index = 0; index < incoming.length; index++) {
      const existingIndex = existingIndexById.get(incoming[index].id);
      if (existingIndex === undefined) {
        continue;
      }

      if (firstIncomingOverlap < 0) {
        firstIncomingOverlap = index;
        firstExistingOverlap = existingIndex;
      }

      lastIncomingOverlap = index;
      lastExistingOverlap = existingIndex;
    }

    if (firstIncomingOverlap >= 0) {
      return this.dedupePosts([
        ...existing.slice(0, firstExistingOverlap),
        ...incoming,
        ...existing.slice(lastExistingOverlap + 1),
      ]);
    }

    return this.dedupePosts(direction === "previous"
      ? [...incoming, ...existing]
      : [...existing, ...incoming]);
  }

  private dedupePosts(posts: DamebooruPostDto[]): DamebooruPostDto[] {
    const seen = new Set<number>();
    const deduped: DamebooruPostDto[] = [];

    for (const post of posts) {
      if (seen.has(post.id)) {
        continue;
      }

      seen.add(post.id);
      deduped.push(post);
    }

    return deduped;
  }

  private trimNavigationPosts(posts: DamebooruPostDto[], currentIndex: number) {
    const start = Math.max(0, currentIndex - this.keepBehindCount);
    const end = Math.min(posts.length, currentIndex + this.keepAheadCount + 1);

    return {
      posts: posts.slice(start, end),
      start,
      end,
    };
  }

  private completePendingEdgeNavigation(direction: NavigationFetchDirection) {
    if (this.pendingEdgeNavigation !== direction) {
      return;
    }

    this.pendingEdgeNavigation = null;
    const adjacentPost = this.getAdjacentPost(direction);
    if (adjacentPost) {
      this.navigateToPostId(adjacentPost.id);
    }
  }

  private getAdjacentPost(direction: NavigationFetchDirection): DamebooruPostDto | null {
    const index = this.currentWindowIndex();
    if (index < 0) {
      return null;
    }

    const offset = direction === "previous" ? -1 : 1;
    return this.navigationPosts()[index + offset] ?? null;
  }

  private replacePostInWindow(updatedPost: DamebooruPostDto) {
    this.navigationPosts.update((posts) => {
      const index = posts.findIndex((post) => post.id === updatedPost.id);
      if (index < 0) {
        return posts;
      }

      const next = [...posts];
      next[index] = updatedPost;
      return next;
    });
  }

  private preloadNearbyPreviews(posts: DamebooruPostDto[], currentIndex: number) {
    const start = Math.max(0, currentIndex - this.previewPreloadBehindCount);
    const end = Math.min(posts.length, currentIndex + this.previewPreloadAheadCount + 1);
    const nearbyPosts = posts.slice(start, end);
    const currentPost = posts[currentIndex];
    const aheadPosts = nearbyPosts.filter((_, index) => start + index > currentIndex);
    const behindPosts = nearbyPosts.filter((_, index) => start + index < currentIndex).reverse();

    const urls = [
      currentPost,
      ...aheadPosts,
      ...behindPosts,
    ]
      .filter((post): post is DamebooruPostDto => post !== undefined)
      .filter((post) => this.getMediaType(post.contentType) !== 'video')
      .map((post) => this.getPreviewUrl(post));

    this.imagePreloadService.preload(urls, {
      concurrency: 4,
      replaceQueue: true,
    });
  }

  private setupHotkeys() {
    // Navigate Left
    this.hotkeys
      .on("ArrowLeft")
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((event) => {
        this.setNavigationKeyHeld(event.key, true);
        this.goToPrevPost();
      });

    // Navigate Right
    this.hotkeys
      .on("ArrowRight")
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((event) => {
        this.setNavigationKeyHeld(event.key, true);
        this.goToNextPost();
      });

    fromEvent<KeyboardEvent>(this.document, "keyup")
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((event) => {
        if (event.key === "ArrowLeft" || event.key === "ArrowRight") {
          this.setNavigationKeyHeld(event.key, false);
        }
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

  private setNavigationKeyHeld(key: string, held: boolean) {
    if (held) {
      this.heldNavigationKeys.add(key);
    } else {
      this.heldNavigationKeys.delete(key);
    }

    this.suppressFullImage.set(this.heldNavigationKeys.size > 0);
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

  onSourceTabOpen(): void {
    this.activeSidebarTabId = "source";
    const id = Number(this.id());
    if (!Number.isInteger(id) || id < 1 || this.autoTagStatusLoadedForPostId === id) {
      return;
    }

    this.loadAutoTagStatus(id);
  }

  onAuditTabOpen(): void {
    this.activeSidebarTabId = "audit";
    const id = Number(this.id());
    if (!Number.isInteger(id) || id < 1 || this.auditLoadedForPostId === id) {
      return;
    }

    this.loadAudit(id, false);
  }

  onSidebarTabOpen(tabId: "info" | "similar"): void {
    this.activeSidebarTabId = tabId;
  }

  goToPrevPost() {
    const previousPost = this.getAdjacentPost("previous");
    if (previousPost) {
      this.navigateToPostId(previousPost.id);
      return;
    }

    const id = this.currentPostId();
    if (id !== null && this.navigationHasPrevious()) {
      this.requestNavigationWindow(
        id,
        this.refillAheadCount,
        this.refillOverlapCount,
        "previous",
        true,
      );
    }
  }

  goToNextPost() {
    const nextPost = this.getAdjacentPost("next");
    if (nextPost) {
      this.navigateToPostId(nextPost.id);
      return;
    }

    const id = this.currentPostId();
    if (id !== null && this.navigationHasNext()) {
      this.requestNavigationWindow(
        id,
        this.refillOverlapCount,
        this.refillAheadCount,
        "next",
        true,
      );
    }
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
        this.replacePostInWindow(updatedPost);
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
          this.replacePostInWindow(result.post);
          this.reloadAuditForCurrentPost();
          this.reloadAutoTagStatusForCurrentPost();

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

  applyAiTags() {
    const post = this.post();
    if (!post || this.isAiTagPreviewLoading()) return;

    this.isAiTagPreviewLoading.set(true);
    this.aiTagPreview.set(null);
    this.damebooru.applyAiTags(post.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: result => {
          this.replacePostInWindow(result.post);
          this.aiTagPreview.set(result.preview);
          this.reloadAuditForCurrentPost();

          const parts: string[] = [];
          if (result.addedTags > 0) parts.push(`added ${result.addedTags} tag${result.addedTags === 1 ? '' : 's'}`);
          if (result.removedTags > 0) parts.push(`removed ${result.removedTags} tag${result.removedTags === 1 ? '' : 's'}`);
          if (result.updatedTagCategories > 0) parts.push(`updated ${result.updatedTagCategories} categor${result.updatedTagCategories === 1 ? 'y' : 'ies'}`);

          this.toastService.success(parts.length > 0 ? `AI tagging complete: ${parts.join(', ')}` : 'AI tagging complete with no changes');
          this.isAiTagPreviewLoading.set(false);
        },
        error: error => {
          this.toastService.error(error?.error || 'AI tag preview failed');
          this.isAiTagPreviewLoading.set(false);
        },
      });
  }

  closeAiTagPreview(): void {
    this.aiTagPreview.set(null);
  }

  getAppliedAiTagCount(preview: AiTagPreview): number {
    return preview.tags.filter(tag => tag.meetsApplyThreshold).length;
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
          this.replacePostInWindow(updatedPost);
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

  getPreviewUrl(post: DamebooruPostDto): string {
    return this.damebooru.getPreviewUrl(
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

  private navigateToPostId(postId: number | null | undefined) {
    if (!postId) return;

    if (this.editService.isEditing()) {
      this.editService.cancelEditing();
    }

    this.zoomPan()?.resetZoom();
    this.closeMobileImageViewer();

    this.router.navigate(AppLinks.post(postId), {
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
    if (this.activeSidebarTabId !== "audit") {
      return;
    }

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
    this.auditRequestPostId = postId;
    this.damebooru
      .getPostAudit(postId, beforeId, 50)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          if (this.auditRequestPostId !== postId || Number(this.id()) !== postId) {
            if (this.auditRequestPostId === postId) {
              this.auditRequestInFlight = false;
              this.auditRequestPostId = null;
            }
            return;
          }

          this.auditLoadedForPostId = postId;
          if (append) {
            this.auditEntries.set([...currentEntries, ...result.items]);
          } else {
            this.auditEntries.set(result.items);
          }

          this.auditHasMore.set(result.hasMore);
          this.auditRequestInFlight = false;
          this.auditRequestPostId = null;
        },
        error: () => {
          if (this.auditRequestPostId === postId) {
            this.auditRequestInFlight = false;
            this.auditRequestPostId = null;
          }
        },
      });
  }

  private loadAutoTagStatus(postId: number) {
    if (this.autoTagStatusRequestInFlight) {
      return;
    }

    this.autoTagStatusRequestInFlight = true;
    this.autoTagStatusRequestPostId = postId;
    this.isAutoTagStatusLoading.set(true);
    this.damebooru
      .getPostAutoTagStatus(postId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: status => {
          if (this.autoTagStatusRequestPostId !== postId || Number(this.id()) !== postId) {
            if (this.autoTagStatusRequestPostId === postId) {
              this.autoTagStatusRequestInFlight = false;
              this.autoTagStatusRequestPostId = null;
            }
            return;
          }

          this.autoTagStatusLoadedForPostId = postId;
          this.autoTagStatus.set(status);
          this.autoTagStatusRequestInFlight = false;
          this.autoTagStatusRequestPostId = null;
          this.isAutoTagStatusLoading.set(false);
        },
        error: () => {
          if (this.autoTagStatusRequestPostId !== postId || Number(this.id()) !== postId) {
            if (this.autoTagStatusRequestPostId === postId) {
              this.autoTagStatusRequestInFlight = false;
              this.autoTagStatusRequestPostId = null;
            }
            return;
          }

          this.autoTagStatusLoadedForPostId = postId;
          this.autoTagStatus.set(null);
          this.autoTagStatusRequestInFlight = false;
          this.autoTagStatusRequestPostId = null;
          this.isAutoTagStatusLoading.set(false);
        },
      });
  }

  private reloadAutoTagStatusForCurrentPost() {
    if (this.activeSidebarTabId !== "source") {
      return;
    }

    const id = Number(this.id());
    if (!Number.isInteger(id) || id < 1) {
      return;
    }

    this.loadAutoTagStatus(id);
  }

  private resetOnDemandTabData(postId: number | null) {
    this.aiTagPreview.set(null);
    this.isAiTagPreviewLoading.set(false);

    if (this.auditLoadedForPostId !== postId) {
      this.auditEntries.set([]);
      this.auditHasMore.set(false);
      this.auditLoadedForPostId = null;
      this.auditRequestInFlight = false;
      this.auditRequestPostId = null;
    }

    if (this.autoTagStatusLoadedForPostId !== postId) {
      this.autoTagStatus.set(null);
      this.autoTagStatusLoadedForPostId = null;
      this.autoTagStatusRequestInFlight = false;
      this.autoTagStatusRequestPostId = null;
      this.isAutoTagStatusLoading.set(false);
    }

    if (postId === null) {
      return;
    }

    if (this.activeSidebarTabId === "audit") {
      this.loadAudit(postId, false);
    }

    if (this.activeSidebarTabId === "source") {
      this.loadAutoTagStatus(postId);
    }
  }

  private getNavigationScopeKey(): string {
    return JSON.stringify({
      query: this.query() ?? "",
      explorerLibraryId: this.activeExplorerLibraryId(),
      explorerPath: this.activeExplorerPath(),
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
