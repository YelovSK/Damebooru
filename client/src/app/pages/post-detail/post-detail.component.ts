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
} from "@angular/core";
import { CommonModule } from "@angular/common";
import { RouterLink, Router } from "@angular/router";
import { HttpClient } from "@angular/common/http";
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

import { BakabooruService } from "@services/api/bakabooru/bakabooru.service";
import { SettingsService } from "@services/settings.service";
import { ToastService } from "@services/toast.service";
import { TagPipe } from "@shared/pipes/escape-tag.pipe";
import { escapeTagName, getMediaType } from "@shared/utils/utils";
import {
  BakabooruPostDto,
  BakabooruPostsAroundDto,
  BakabooruTagDto,
  ManagedTagCategory,
  PostTagSource,
  SimilarPost,
} from "@models";
import { ButtonComponent } from "@shared/components/button/button.component";
import { AutocompleteComponent } from "@shared/components/autocomplete/autocomplete.component";
import { AutoTaggingResultsComponent } from "@shared/components/auto-tagging-results/auto-tagging-results.component";
import { ProgressiveImageComponent } from "@shared/components/progressive-image/progressive-image.component";
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
    AutoTaggingResultsComponent,
    ProgressiveImageComponent,
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
  private readonly bakabooru = inject(BakabooruService);
  private readonly router = inject(Router);
  private readonly http = inject(HttpClient);
  private readonly hotkeys = inject(HotkeysService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly settingsService = inject(SettingsService);
  private readonly toastService = inject(ToastService);
  readonly editService = inject(PostEditService);

  readonly appLinks = AppLinks;
  readonly postTagSource = PostTagSource;

  // Video settings from user preferences
  readonly autoPlayVideos = this.settingsService.autoPlayVideos;
  readonly startVideosMuted = this.settingsService.startVideosMuted;

  id = input.required<string>();
  query = input<string | null>("");

  // Sidebar collapsed state
  sidebarCollapsed = signal(false);

  private readonly zoomPan = viewChild<ZoomPanContainerComponent>("zoomPan");

  private swipePointerId: number | null = null;
  private swipeStartX: number | null = null;
  private swipeStartY: number | null = null;
  private swipeStartTime = 0;
  private readonly swipeMinDistancePx = 60;
  private readonly swipeMaxDurationMs = 700;
  private readonly swipeDirectionRatio = 1.3;

  // Triggers a local post stream refresh after in-place edits.
  private refreshTrigger = signal(0);
  private readonly postCache = signal(new Map<number, BakabooruPostDto>());

  // Registered auto-tagging providers
  registeredProviders = computed(() =>
    this.editService.getRegisteredProviders(),
  );

  // Tag autocomplete for edit mode
  private tagQuery$ = new Subject<string>();
  tagSuggestions = toSignal(
    this.tagQuery$.pipe(
      switchMap((word) => {
        if (word.length < 1) return of([]);
        return this.bakabooru.getTags(`*${word}* sort:usages`, 0, 10).pipe(
          map((res) => res.results),
          catchError(() => of([])),
        );
      }),
      takeUntilDestroyed(this.destroyRef),
    ),
    { initialValue: [] as BakabooruTagDto[] },
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
    combineLatest([toObservable(this.id), toObservable(this.query)]).pipe(
      switchMap(([id, query]) =>
        this.bakabooru.getPostsAround(Number(id), query!).pipe(
          tap((around) => {
            if (around.prev) this.setCachedPost(around.prev);
            if (around.next) this.setCachedPost(around.next);
          }),
          catchError((err) => {
            console.error(
              "Around API failed (disabling keyboard nav for this post):",
              err,
            );
            return of({ prev: null, next: null } as BakabooruPostsAroundDto);
          }),
        ),
      ),
    ),
  );

  // Fetch tag categories for proper tag coloring
  tagCategories = toSignal(
    this.bakabooru.getTagCategories().pipe(
      catchError(() => {
        console.error("Failed to load tag categories");
        return of([]);
      }),
    ),
    { initialValue: [] as ManagedTagCategory[] },
  );

  similarPosts = computed<SimilarPost[]>(() => this.post()?.similarPosts ?? []);

  imageLoading = signal(true);

  constructor() {
    effect(() => {
      this.post();
      this.imageLoading.set(true);
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
  }

  private getPostWithCache(id: number) {
    const cached = this.postCache().get(id);
    if (cached) {
      return of(cached);
    }

    return this.bakabooru
      .getPost(id)
      .pipe(tap((post) => this.setCachedPost(post)));
  }

  private setCachedPost(post: BakabooruPostDto) {
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
  }

  getTagCategory(tag: BakabooruTagDto): ManagedTagCategory | undefined {
    if (!tag.categoryName) return undefined;
    return this.tagCategories().find((cat) => cat.name === tag.categoryName);
  }

  hasTagSource(tag: BakabooruTagDto, source: PostTagSource): boolean {
    return tag.source === source;
  }

  // Sidebar toggle
  toggleSidebar() {
    this.sidebarCollapsed.update((v) => !v);
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
  }

  onMediaPointerUp(event: PointerEvent) {
    if (this.swipePointerId !== event.pointerId) return;
    if (this.swipeStartX === null || this.swipeStartY === null) return;

    const deltaX = event.clientX - this.swipeStartX;
    const deltaY = event.clientY - this.swipeStartY;
    const elapsedMs = performance.now() - this.swipeStartTime;
    this.resetSwipeState();

    if (elapsedMs > this.swipeMaxDurationMs) return;
    if (Math.abs(deltaX) < this.swipeMinDistancePx) return;
    if (Math.abs(deltaX) < Math.abs(deltaY) * this.swipeDirectionRatio) return;

    if (deltaX < 0) {
      this.goToNextPost();
      return;
    }

    this.goToPrevPost();
  }

  onMediaPointerCancel() {
    this.resetSwipeState();
  }

  private resetSwipeState() {
    this.swipePointerId = null;
    this.swipeStartX = null;
    this.swipeStartY = null;
    this.swipeStartTime = 0;
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

  onTagSelection(tag: BakabooruTagDto) {
    this.editService.addTag(tag.name, tag.id);
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

  getTagSourceLabel(source: PostTagSource): string {
    switch (source) {
      case PostTagSource.Manual:
        return 'manual';
      case PostTagSource.Folder:
        return 'folder';
      case PostTagSource.Ai:
        return 'ai';
      default:
        return 'unknown';
    }
  }

  protected trackByEditTag(tag: PostEditTag): string {
    return `${tag.source}|${tag.name}`;
  }

  // Auto-tagging
  triggerAutoTagging() {
    const post = this.post();
    if (!post) return;

    // Use HttpClient to fetch through the proxy (avoids CORS)
    const url = this.bakabooru.getPostContentUrl(post.id);
    this.http
      .get(url, { responseType: "blob" })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((blob) => {
        const file = new File([blob], `post-${post.id}`, { type: blob.type });
        this.editService.triggerAutoTagging(file, this.destroyRef);
      });
  }

  triggerProviderAutoTagging(providerId: string) {
    const post = this.post();
    if (!post) return;

    const url = this.bakabooru.getPostContentUrl(post.id);
    this.http
      .get(url, { responseType: "blob" })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((blob) => {
        const file = new File([blob], `post-${post.id}`, { type: blob.type });
        this.editService.triggerProviderAutoTagging(
          file,
          providerId,
          this.destroyRef,
        );
      });
  }

  applyAutoTags(providerId: string) {
    const result = this.editService
      .autoTags()
      .find((r) => r.providerId === providerId);
    if (!result) return;

    const stateBefore = this.editService.currentState();
    const tagsBefore = stateBefore?.tags.length || 0;

    this.editService.applyAutoTags(providerId);

    const stateAfter = this.editService.currentState();
    const tagsAfter = stateAfter?.tags.length || 0;
    const added = tagsAfter - tagsBefore;

    if (added > 0) {
      this.toastService.success(
        `Added ${added} tag${added !== 1 ? "s" : ""} from ${result.provider}`,
      );
    } else {
      this.toastService.info(`No new tags added (all already present)`);
    }
  }

  applyAutoSources(providerId: string) {
    const result = this.editService
      .autoTags()
      .find((r) => r.providerId === providerId);
    if (!result) return;

    const stateBefore = this.editService.currentState();
    const sourcesBefore = stateBefore?.sources.length || 0;

    this.editService.applyAutoSources(providerId);

    // Update the textarea
    const state = this.editService.currentState();
    if (state) {
      this.sourcesValue.set(state.sources.join("\n"));
    }

    const sourcesAfter = state?.sources.length || 0;
    const added = sourcesAfter - sourcesBefore;

    if (added > 0) {
      this.toastService.success(
        `Added ${added} source${added !== 1 ? "s" : ""} from ${result.provider}`,
      );
    } else {
      this.toastService.info(`No new sources added (all already present)`);
    }
  }

  toggleFavorite() {
    const post = this.post();
    if (!post) return;

    const operation = post.isFavorite
      ? this.bakabooru.unfavoritePost(post.id)
      : this.bakabooru.favoritePost(post.id);

    operation
      .pipe(switchMap(() => this.bakabooru.getPost(post.id)))
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (updatedPost) => {
          this.setCachedPost(updatedPost);
          this.refreshTrigger.update((n) => n + 1);
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

  getThumbnailUrl(post: BakabooruPostDto): string {
    return this.bakabooru.getThumbnailUrl(
      post.thumbnailLibraryId,
      post.thumbnailContentHash,
    );
  }

  getPostContentUrl(post: BakabooruPostDto): string {
    return this.bakabooru.getPostContentUrl(post.id);
  }

  getSimilarPostThumbnailUrl(post: SimilarPost): string {
    return this.bakabooru.getThumbnailUrl(
      post.thumbnailLibraryId,
      post.thumbnailContentHash,
    );
  }

  private navigateToPost(post: BakabooruPostDto | null | undefined) {
    if (!post) return;

    if (this.editService.isEditing()) {
      this.editService.cancelEditing();
    }

    this.zoomPan()?.resetZoom();

    this.router.navigate(AppLinks.post(post.id), {
      queryParams: { query: this.query() },
      replaceUrl: true,
    });
  }

  protected trackByTag(tag: BakabooruTagDto): string {
    return tag.id.toString() + '|' + tag.source.toString();
  }
}
