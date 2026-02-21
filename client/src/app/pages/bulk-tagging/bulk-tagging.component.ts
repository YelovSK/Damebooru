import {
  Component,
  inject,
  signal,
  computed,
  ChangeDetectionStrategy,
  DestroyRef,
  input,
} from "@angular/core";
import { CommonModule } from "@angular/common";
import { FormsModule } from "@angular/forms";
import { RouterLink, Router } from "@angular/router";
import {
  toObservable,
  takeUntilDestroyed,
  toSignal,
} from "@angular/core/rxjs-interop";
import { Subject, switchMap, of, map, catchError, combineLatest } from "rxjs";

import { DamebooruService } from "@services/api/damebooru/damebooru.service";
import {
  AutoTaggingService,
  TaggingEntry,
} from "@services/auto-tagging/auto-tagging.service";
import { RateLimiterService } from "@services/rate-limiting/rate-limiter.service";
import { ToastService } from "@services/toast.service";
import {
  DamebooruPostDto,
  DamebooruPostListDto,
  DamebooruTagDto,
  PostTagSource,
  UpdatePostTagInput,
} from "@models";
import { ButtonComponent } from "@shared/components/button/button.component";
import { FormCheckboxComponent } from "@shared/components/form-checkbox/form-checkbox.component";
import { PaginatorComponent } from "@shared/components/paginator/paginator.component";
import { AutocompleteComponent } from "@shared/components/autocomplete/autocomplete.component";
import { AppLinks } from "@app/app.paths";
import { escapeTagName } from "@shared/utils/utils";
import type { TaggingState } from "@services/tagging/models";

interface BulkTaggingState {
  data: DamebooruPostListDto | null;
  isLoading: boolean;
  error: unknown;
}

interface PostTaggingStatus {
  post: DamebooruPostDto;
  state: TaggingState;
  result?: TaggingEntry;
}

@Component({
  selector: "app-bulk-tagging",
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    ButtonComponent,
    FormCheckboxComponent,
    PaginatorComponent,
    AutocompleteComponent,
  ],
  templateUrl: "./bulk-tagging.component.html",
  styleUrl: "./bulk-tagging.component.css",
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BulkTaggingComponent {
  private readonly damebooru = inject(DamebooruService);
  private readonly autoTagging = inject(AutoTaggingService);
  private readonly rateLimiter = inject(RateLimiterService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly appLinks = AppLinks;

  // Query parameters from URL
  queryParam = input<string | null>(null, { alias: "query" });
  offset = input<string | null>("0");

  // Editable search query (for autocomplete)
  currentSearchValue = signal("tag-count:0");

  // Tag suggestions for autocomplete
  private tagQuery$ = new Subject<string>();
  tagSuggestions = toSignal(
    this.tagQuery$.pipe(
      switchMap((word) => {
        if (word.length < 1) return of([]);
        return this.damebooru.getTags(`*${word}* sort:usages`, 0, 15).pipe(
          map((res) => res.results),
          catchError(() => of([])),
        );
      }),
    ),
    { initialValue: [] as DamebooruTagDto[] },
  );

  // Pagination
  pageSize = signal(50);
  currentPage = computed(() => {
    const off = Number(this.offset() ?? "0") || 0;
    return Math.floor(off / this.pageSize()) + 1;
  });

  // Posts state - uses URL query param and includes media key fields
  private postsState$ = combineLatest([
    toObservable(this.queryParam),
    toObservable(this.offset),
  ]).pipe(
    switchMap(([q, off]) => {
      const query = q || "tag-count:0";
      const offsetNum = Number(off ?? "0") || 0;
      return this.damebooru.getPosts(query, offsetNum, this.pageSize()).pipe(
        map(
          (data) =>
            ({ data, isLoading: false, error: null }) as BulkTaggingState,
        ),
        catchError((error) =>
          of({ data: null, isLoading: false, error } as BulkTaggingState),
        ),
      );
    }),
  );

  postsState = toSignal(this.postsState$, {
    initialValue: {
      data: null,
      isLoading: true,
      error: null,
    } as BulkTaggingState,
  });

  totalItems = computed(() => this.postsState().data?.totalCount ?? 0);
  totalPages = computed(() => Math.ceil(this.totalItems() / this.pageSize()));

  // Tagging status per post
  postStatuses = signal<Map<number, PostTaggingStatus>>(new Map());

  // Pause state from AutoTaggingService
  isPaused = this.autoTagging.isPaused;

  // Rate limiter statuses
  rateLimiterStatuses = this.rateLimiter.statuses;

  // UI state
  isTagging = signal(false);
  isFetchingPosts = signal(false);
  selectedPosts = signal<Set<number>>(new Set());

  // Stats
  stats = computed(() => {
    const statuses = this.postStatuses();
    let success = 0;
    let noResults = 0;
    let error = 0;
    let pending = 0;

    statuses.forEach((status) => {
      switch (status.state) {
        case "success":
        case "applied":
          success++;
          break;
        case "no-results":
          noResults++;
          break;
        case "error":
          error++;
          break;
        case "queued":
        case "tagging":
          pending++;
          break;
      }
    });

    return { success, noResults, error, pending, total: statuses.size };
  });

  constructor() {
    // Sync URL query param to search value signal on init
    toObservable(this.queryParam)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((q) => {
        this.currentSearchValue.set(q ?? "tag-count:0");
      });

    // Initialize post statuses when posts load
    toObservable(this.postsState)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((state) => {
        if (state.data) {
          const newStatuses = new Map<number, PostTaggingStatus>();
          for (const post of state.data.items) {
            const existing = this.postStatuses().get(post.id);
            newStatuses.set(post.id, existing ?? { post, state: "idle" });
          }
          this.postStatuses.set(newStatuses);
        }
      });
  }

  // Autocomplete handlers
  onQueryChange(word: string) {
    this.tagQuery$.next(escapeTagName(word));
  }

  onSelection(tag: DamebooruTagDto) {
    const value = this.currentSearchValue().trimEnd();
    const parts = value.split(/\s+/);
    parts[parts.length - 1] = escapeTagName(tag.name);
    const newValue = parts.join(" ") + " ";
    this.currentSearchValue.set(newValue);
    this.tagQuery$.next("");
  }

  onSearch(query: string) {
    this.router.navigate([], {
      queryParams: { query, offset: 0 },
      queryParamsHandling: "merge",
      replaceUrl: true,
    });
  }

  selectAll() {
    const posts = this.postsState().data?.items ?? [];
    this.selectedPosts.set(new Set(posts.map((p) => p.id)));
  }

  selectNone() {
    this.selectedPosts.set(new Set());
  }

  selectByState(state: TaggingState) {
    const statuses = this.postStatuses();
    const selected = new Set<number>();
    statuses.forEach((status, id) => {
      if (status.state === state) {
        selected.add(id);
      }
    });
    this.selectedPosts.set(selected);
  }

  toggleSelect(postId: number) {
    this.selectedPosts.update((s) => {
      const newSet = new Set(s);
      if (newSet.has(postId)) {
        newSet.delete(postId);
      } else {
        newSet.add(postId);
      }
      return newSet;
    });
  }

  isSelected(postId: number): boolean {
    return this.selectedPosts().has(postId);
  }

  async startTagging() {
    const selected = this.selectedPosts();
    if (selected.size === 0) {
      this.toast.show("No posts selected", "warning");
      return;
    }

    // Get posts to tag - only those that are idle (not already queued/tagging/done)
    const statuses = this.postStatuses();
    const postsToTag =
      this.postsState().data?.items.filter((p) => {
        if (!selected.has(p.id)) return false;
        const status = statuses.get(p.id);
        // Only queue posts that are idle
        return status?.state === "idle";
      }) ?? [];

    if (postsToTag.length === 0) {
      this.toast.show(
        "All selected posts are already queued or processed",
        "info",
      );
      return;
    }

    this.isTagging.set(true);
    this.toast.show(
      `Queuing ${postsToTag.length} posts for tagging...`,
      "info",
    );

    let queuedCount = 0;
    let errorCount = 0;

    for (const post of postsToTag) {
      // Update status to queued
      this.updatePostStatus(post.id, "queued");

      // Download the post's content as a File for tagging
      try {
        const contentUrl = this.damebooru.getPostContentUrl(post.id);
        const response = await fetch(contentUrl);
        if (!response.ok) {
          throw new Error(`HTTP ${response.status}`);
        }
        const blob = await response.blob();
        const fileName = `post_${post.id}`;
        const file = new File([blob], fileName, { type: blob.type });

        // Tag using AutoTaggingService
        this.autoTagging
          .queue(file)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({
            next: (entry) => {
              // Map TaggingEntry status to TaggingState
              const state: TaggingState =
                entry.status === "completed"
                  ? entry.results?.some((r) => r.categorizedTags.length > 0)
                    ? "success"
                    : "no-results"
                  : entry.status === "failed"
                    ? "error"
                    : entry.status;
              this.updatePostStatus(post.id, state, entry);
              // Check if all tagging is done
              if (entry.status === "completed" || entry.status === "failed") {
                this.checkTaggingComplete();
              }
            },
            error: (err) => {
              console.error(`Tagging error for post ${post.id}:`, err);
              this.updatePostStatus(post.id, "error");
              this.checkTaggingComplete();
            },
          });
        queuedCount++;
      } catch (err) {
        console.error(`Failed to fetch post ${post.id}:`, err);
        this.updatePostStatus(post.id, "error");
        errorCount++;
      }
    }

    if (queuedCount > 0) {
      this.toast.show(`${queuedCount} posts queued for tagging`, "success");
    }
    if (errorCount > 0) {
      this.toast.show(`${errorCount} posts failed to load`, "error");
    }
  }

  private checkTaggingComplete() {
    const statuses = this.postStatuses();
    const hasActiveTagging = Array.from(statuses.values()).some(
      (s) => s.state === "queued" || s.state === "tagging",
    );
    if (!hasActiveTagging) {
      this.isTagging.set(false);
      this.toast.show("Tagging complete", "success");
    }
  }

  pauseTagging() {
    this.autoTagging.pause();
  }

  resumeTagging() {
    this.autoTagging.resume();
  }

  cancelTagging() {
    this.autoTagging.cancelAll();
    this.isTagging.set(false);

    // Reset queued/tagging posts to idle
    this.postStatuses.update((map) => {
      const newMap = new Map(map);
      newMap.forEach((status, id) => {
        if (status.state === "queued" || status.state === "tagging") {
          newMap.set(id, { ...status, state: "idle" });
        }
      });
      return newMap;
    });
  }

  applyTagsToPost(postId: number) {
    const status = this.postStatuses().get(postId);
    if (!status?.result?.results) return;

    // Collect all tags from results
    const allTags = new Set<string>();
    for (const result of status.result.results) {
      for (const tag of result.categorizedTags) {
        allTags.add(tag.name);
      }
    }

    if (allTags.size === 0) {
      this.toast.show("No tags to apply", "warning");
      return;
    }

    // Get current post and update
    this.damebooru
      .getPost(postId)
      .pipe(
        switchMap((post) => {
          if (!post) throw new Error("Post not found");
          const normalizedAutoTagNames = new Set(
            Array.from(allTags, (tag) => tag.trim().toLowerCase()).filter(
              (tag) => tag.length > 0,
            ),
          );

          const tagsWithSources: UpdatePostTagInput[] = post.tags.map((tag) => ({
            tagId: tag.id,
            name: tag.name,
            source: tag.source,
          }));

          const existingManualTagNames = new Set(
            post.tags
              .filter((tag) => tag.source === PostTagSource.Manual)
              .map((tag) => tag.name.toLowerCase()),
          );

          for (const tagName of normalizedAutoTagNames) {
            if (existingManualTagNames.has(tagName)) {
              continue;
            }

            existingManualTagNames.add(tagName);
            tagsWithSources.push({
              name: tagName,
              source: PostTagSource.Manual,
            });
          }

          return this.damebooru.updatePost(postId, { tagsWithSources });
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: () => {
          this.toast.show(
            `Applied ${allTags.size} tags to post #${postId}`,
            "success",
          );
          this.updatePostStatus(postId, "applied");
        },
        error: (err) => {
          this.toast.show(`Failed to apply tags: ${err.message}`, "error");
        },
      });
  }

  applyTagsToAll() {
    const successPosts = Array.from(this.postStatuses().entries())
      .filter(([, status]) => status.state === "success")
      .map(([id]) => id);

    if (successPosts.length === 0) {
      this.toast.show("No posts with tags to apply", "warning");
      return;
    }

    for (const postId of successPosts) {
      this.applyTagsToPost(postId);
    }
  }

  getStateIcon(state: TaggingState): string {
    switch (state) {
      case "idle":
        return "○";
      case "queued":
        return "⏳";
      case "tagging":
        return "🔄";
      case "success":
        return "✅";
      case "no-results":
        return "⚠️";
      case "error":
        return "❌";
      case "applied":
        return "✓";
      default:
        return "?";
    }
  }

  getStateClass(state: TaggingState): string {
    switch (state) {
      case "success":
      case "applied":
        return "border-l-status-success";
      case "no-results":
        return "border-l-status-warning";
      case "error":
        return "border-l-status-error";
      case "tagging":
        return "border-l-accent-primary animate-pulse";
      case "queued":
        return "border-l-text-dim";
      default:
        return "border-l-transparent";
    }
  }

  onPageChange(page: number) {
    if (page < 1 || page > this.totalPages()) return;
    const newOffset = (page - 1) * this.pageSize();
    this.router.navigate([], {
      queryParams: { offset: newOffset },
      queryParamsHandling: "merge",
      replaceUrl: true,
    });
  }

  private updatePostStatus(
    postId: number,
    state: TaggingState,
    result?: TaggingEntry,
  ) {
    this.postStatuses.update((map) => {
      const newMap = new Map(map);
      const existing = newMap.get(postId);
      if (existing) {
        newMap.set(postId, {
          ...existing,
          state,
          result: result ?? existing.result,
        });
      }
      return newMap;
    });
  }

  getActivePostIds(): number[] {
    const result: number[] = [];
    this.postStatuses().forEach((status, id) => {
      if (status.state === "tagging") {
        result.push(id);
      }
    });
    return result;
  }

  getQueuedPostIds(): number[] {
    const result: number[] = [];
    this.postStatuses().forEach((status, id) => {
      if (status.state === "queued") {
        result.push(id);
      }
    });
    return result;
  }

  getTotalTagCount(results: { categorizedTags: unknown[] }[]): number {
    return results.reduce((sum, r) => sum + r.categorizedTags.length, 0);
  }

  getThumbnailUrl(post: DamebooruPostDto): string {
    return this.damebooru.getThumbnailUrl(
      post.thumbnailLibraryId,
      post.thumbnailContentHash,
    );
  }

  getActiveBackoff(): {
    apiId: string;
    waitMs: number;
    backoffLevel: number;
  } | null {
    const statuses = this.rateLimiterStatuses();
    for (const [apiId, status] of Object.entries(statuses)) {
      if (status.inBackoff) {
        return { apiId, ...status };
      }
    }
    return null;
  }
}
