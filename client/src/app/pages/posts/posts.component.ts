import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  HostListener,
  ViewChild,
  computed,
  effect,
  inject,
  input,
  signal,
  NgZone,
} from "@angular/core";
import { CommonModule } from "@angular/common";
import { Router } from "@angular/router";
import {
  CdkVirtualScrollViewport,
  ScrollingModule,
} from "@angular/cdk/scrolling";
import {
  toObservable,
  toSignal,
  takeUntilDestroyed,
} from "@angular/core/rxjs-interop";
import {
  Subject,
  auditTime,
  catchError,
  combineLatest,
  distinctUntilChanged,
  fromEvent,
  map,
  of,
  switchMap,
} from "rxjs";

import { DamebooruService } from "@services/api/damebooru/damebooru.service";
import { HotkeysService } from "@services/hotkeys.service";
import { DamebooruTagDto } from "@models";
import { AutocompleteComponent } from "@shared/components/autocomplete/autocomplete.component";
import { escapeTagName } from "@shared/utils/utils";
import { AppLinks, AppPaths } from "@app/app.paths";
import { StorageService, STORAGE_KEYS } from "@services/storage.service";
import { SettingsService } from "@services/settings.service";
import { PostPreviewOverlayComponent } from "@shared/components/post-preview-overlay/post-preview-overlay.component";
import { PostTileComponent } from "@shared/components/post-tile/post-tile.component";
import {
  offsetToPage,
} from "./posts-row-math";
import { POSTS_PAGE_SIZE } from "./posts.constants";
import { VirtualRowIndexDataSource } from "./posts-row-index.data-source";
import { PostsPageCacheStore } from "./posts-page-cache.store";
import { PostsFastScrollerController } from "./posts-fast-scroller.controller";
import {
  GridCell,
  GridDensity,
  PageStatus,
  RouteState,
} from "./posts.types";

@Component({
  selector: "app-posts",
  imports: [
    CommonModule,
    AutocompleteComponent,
    ScrollingModule,
    PostPreviewOverlayComponent,
    PostTileComponent,
  ],
  providers: [PostsPageCacheStore, PostsFastScrollerController],
  templateUrl: "./posts.component.html",
  styleUrl: "./posts.component.css",
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PostsComponent implements AfterViewInit {
  private static readonly GRID_GAP_PX = 8;
  private static readonly MOBILE_BREAKPOINT_PX = 768;
  private static readonly MIN_TILE_SIZE_PX = 56;
  private static readonly TOOLBAR_SHOW_TOP_THRESHOLD_PX = 20;
  private static readonly TOOLBAR_HIDE_SCROLL_DELTA_PX = 40;
  private static readonly TOOLBAR_SHOW_SCROLL_DELTA_PX = 150;
  private static readonly TOOLBAR_TOGGLE_GUARD_MS = 120;
  private static readonly MOBILE_TOOLBAR_TOP_MARGIN_PX = 12;

  private static readonly MIN_VIEWPORT_HEIGHT_PX = 260;
  private static readonly VIEWPORT_BOTTOM_GUTTER_PX = 0;

  private static readonly URL_SYNC_DEBOUNCE_MS = 160;

  private static readonly SCROLL_IDLE_RESET_MS = 120;
  private static readonly HOVER_PREVIEW_DELAY_MS = 700;

  @ViewChild("viewportShell")
  private viewportShellRef?: ElementRef<HTMLElement>;
  @ViewChild("toolbar") private toolbarRef?: ElementRef<HTMLElement>;
  @ViewChild("postsViewport") private viewportRef?: CdkVirtualScrollViewport;
  @ViewChild("fastScrollerRail")
  private fastScrollerRailRef?: ElementRef<HTMLElement>;

  private gridResizeObserver?: ResizeObserver;
  private toolbarResizeObserver?: ResizeObserver;

  private readonly rowCellsCache = new Map<string, GridCell[]>();

  private pendingAnchorOffset: number | null = 0;
  private routeAnchorAppliedForQuery: string | null = null;

  private latestMeasuredOffset = 0;
  private lastToolbarScrollTop = 0;
  private toolbarScrollAccumulator = 0;
  private toolbarToggleLockUntilMs = 0;

  private urlSyncTimer: ReturnType<typeof setTimeout> | null = null;
  private scrollIdleTimer: ReturnType<typeof setTimeout> | null = null;
  private hoverPreviewTimer: ReturnType<typeof setTimeout> | null = null;
  private scrollRafId: number | null = null;

  private readonly damebooru = inject(DamebooruService);
  private readonly router = inject(Router);
  private readonly storage = inject(StorageService);
  private readonly settingsService = inject(SettingsService);
  private readonly hotkeys = inject(HotkeysService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly pageCacheStore = inject(PostsPageCacheStore);
  private readonly fastScroller = inject(PostsFastScrollerController);
  private readonly zone = inject(NgZone);

  readonly appLinks = AppLinks;
  readonly appPaths = AppPaths;

  query = input<string | null>("");
  page = input<string | null>(null);
  offset = input<string | null>("0");

  currentSearchValue = signal("");

  readonly densityOptions: readonly {
    value: GridDensity;
    label: string;
    targetPx: number;
  }[] = [
      { value: "compact", label: "Compact", targetPx: 150 },
      { value: "comfortable", label: "Comfortable", targetPx: 200 },
      { value: "cozy", label: "Cozy", targetPx: 260 },
    ];

  density = signal<GridDensity>(this.getInitialDensity());

  activeQuery = signal("");
  readonly activeQueryParams = computed(() => ({
    query: this.activeQuery() || null,
  }));
  readonly totalCount = this.pageCacheStore.totalCount;
  readonly isInitialLoading = this.pageCacheStore.isInitialLoading;

  readonly pageCache = this.pageCacheStore.pageCache;
  readonly hoverPreviewEnabled = this.settingsService.enablePostPreviewOnHover;
  readonly hoverPreviewDelayMs = computed(() => {
    const raw = this.settingsService.postPreviewDelayMs();
    if (!Number.isFinite(raw)) {
      return PostsComponent.HOVER_PREVIEW_DELAY_MS;
    }

    return Math.max(0, Math.min(5000, Math.round(raw)));
  });

  anchorPageHint = signal(1);

  viewportHeightPx = signal(480);
  columns = signal(1);
  tileSizePx = signal(150);

  currentOffset = signal(0);
  isScrolling = signal(false);
  toolbarHidden = signal(false);
  previewPost = signal<import("@models").DamebooruPostDto | null>(null);
  isMobileViewport = signal(false);
  toolbarHeightPx = signal(0);

  readonly mobileToolbarInsetPx = computed(() =>
    this.isMobileViewport() && !this.toolbarHidden()
      ? this.toolbarHeightPx() + PostsComponent.MOBILE_TOOLBAR_TOP_MARGIN_PX
      : 0,
  );

  readonly totalPages = computed(() => {
    const total = this.totalCount();
    if (total === null || total <= 0) {
      return 0;
    }

    return Math.ceil(total / POSTS_PAGE_SIZE);
  });

  readonly rowItemHeightPx = computed(
    () => this.tileSizePx() + PostsComponent.GRID_GAP_PX,
  );
  readonly virtualRowCount = computed(() => {
    const columns = Math.max(1, this.columns());
    const total = this.totalCount();
    if (total === null) {
      return Math.max(1, Math.ceil(POSTS_PAGE_SIZE / columns));
    }

    return Math.ceil(Math.max(0, total) / columns);
  });
  readonly virtualRowDataSource = new VirtualRowIndexDataSource();

  readonly hasNoResults = computed(
    () => this.totalCount() === 0 && !this.isInitialLoading(),
  );

  readonly currentPage = computed(() =>
    offsetToPage(this.currentOffset(), POSTS_PAGE_SIZE),
  );

  readonly gridTemplateColumns = computed(
    () => `repeat(${this.columns()}, minmax(0, 1fr))`,
  );

  readonly fastScrollerVisible = this.fastScroller.visible;
  readonly fastScrollerDragging = this.fastScroller.dragging;
  readonly fastScrollerThumbTopPx = this.fastScroller.thumbTopPx;
  readonly fastScrollerThumbHeightPx = this.fastScroller.thumbHeightPx;
  readonly fastScrollerBubbleTopPx = this.fastScroller.bubbleTopPx;
  readonly fastScrollerBubblePage = this.fastScroller.bubblePage;

  readonly showFastScrollerBubble = computed(
    () => this.fastScrollerVisible() && this.totalPages() > 1,
  );
  readonly virtualMinBufferRows = computed(() =>
    this.fastScrollerDragging() ? 4 : 12,
  );
  readonly virtualMaxBufferRows = computed(() =>
    this.fastScrollerDragging() ? 8 : 24,
  );

  private tagQuery$ = new Subject<string>();
  tagSuggestions = toSignal(
    this.tagQuery$.pipe(
      switchMap((word) => {
        if (word.length < 1) {
          return of([]);
        }

        return this.damebooru.getTags(`*${word}* sort:usages`, 0, 15).pipe(
          map((res) => res.results),
          catchError(() => of([])),
        );
      }),
    ),
    { initialValue: [] as DamebooruTagDto[] },
  );

  constructor() {
    effect(() => {
      this.virtualRowDataSource.setLength(this.virtualRowCount());
    });

    effect(() => {
      this.pageCache();
      this.rowCellsCache.clear();
    });

    effect(() => {
      const total = this.totalCount();
      if (total === null) {
        return;
      }

      this.clampAnchorsToTotal();
      queueMicrotask(() => {
        this.tryApplyPendingAnchor();
        this.refreshFastScrollerGeometry();
      });
    });

    effect(() => {
      if (this.hoverPreviewEnabled()) {
        return;
      }

      this.clearHoverPreviewTimer();
      this.previewPost.set(null);
    });

    this.fastScroller.configure({
      getRailElement: () => this.fastScrollerRailRef?.nativeElement ?? null,
      getTotalPages: () => this.totalPages(),
      getViewportMetrics: () => this.getFastScrollerViewportMetrics(),
      scrollToOffset: (scrollTop) =>
        this.viewportRef?.scrollToOffset(scrollTop, "auto"),
      resolvePageForScrollTop: (scrollTop) =>
        this.resolvePageForScrollTop(scrollTop),
      resolveOffsetForScrollTop: (scrollTop) =>
        this.resolveOffsetForScrollTop(scrollTop),
      onDragSample: (offset, page) =>
        this.handleFastScrollerDragSample(offset, page),
      onDragCommit: () => this.onFastScrollerDragCommit(),
    });

    combineLatest([
      toObservable(this.query).pipe(map((value) => value ?? "")),
      toObservable(this.page).pipe(
        map((value) => this.parsePositiveInt(value)),
      ),
      toObservable(this.offset).pipe(
        map((value) => this.parseNonNegativeInt(value)),
      ),
    ])
      .pipe(
        map(([query, page, offset]) => ({ query, page, offset }) as RouteState),
        distinctUntilChanged(
          (left, right) =>
            left.query === right.query &&
            left.page === right.page &&
            left.offset === right.offset,
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((state) => this.handleRouteState(state));

    this.destroyRef.onDestroy(() => {
      this.gridResizeObserver?.disconnect();
      this.toolbarResizeObserver?.disconnect();
      this.fastScroller.dispose();
      this.clearUrlSyncTimer();
      this.clearScrollIdleTimer();
      this.clearHoverPreviewTimer();
      this.virtualRowDataSource.disconnect();
      if (this.scrollRafId !== null) {
        cancelAnimationFrame(this.scrollRafId);
        this.scrollRafId = null;
      }
    });

    this.setupHotkeys();
  }

  ngAfterViewInit(): void {
    const shell = this.viewportShellRef?.nativeElement;
    if (!shell) {
      return;
    }

    this.gridResizeObserver = new ResizeObserver(() =>
      this.recalculateLayout(false),
    );
    this.gridResizeObserver.observe(shell);
    this.setupToolbarObserver();

    fromEvent(window, "resize")
      .pipe(auditTime(120), takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.recalculateLayout(false));

    const viewport = this.viewportRef;
    if (viewport) {
      viewport.renderedRangeStream
        .pipe(auditTime(40), takeUntilDestroyed(this.destroyRef))
        .subscribe((range) => this.handleRenderedRange(range.start, range.end));

      viewport
        .elementScrolled()
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe(() => {
          if (this.scrollRafId !== null) {
            return;
          }

          this.scrollRafId = requestAnimationFrame(() => {
            this.scrollRafId = null;
            this.zone.run(() => this.onViewportScrolled());
          });
        });

      this.zone.runOutsideAngular(() => {
        const el = viewport.elementRef.nativeElement;
        fromEvent<MouseEvent>(el, "mouseover")
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe((e) => this.handleViewportMouseOver(e));
        fromEvent<MouseEvent>(el, "mouseout")
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe((e) => this.handleViewportMouseOut(e));
      });
    }

    const rail = this.fastScrollerRailRef?.nativeElement;
    if (rail) {
      this.zone.runOutsideAngular(() => {
        fromEvent<PointerEvent>(rail, "pointerdown")
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe((e) => this.onFastScrollerPointerDown(e));
        fromEvent<PointerEvent>(rail, "pointermove")
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe((e) => this.onFastScrollerPointerMove(e));
        fromEvent<PointerEvent>(rail, "pointerup")
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe((e) => this.onFastScrollerPointerUp(e));
        fromEvent<PointerEvent>(rail, "pointercancel")
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe((e) => this.onFastScrollerPointerCancel(e));
      });
    }

    queueMicrotask(() => {
      this.recalculateLayout(false);
      this.tryApplyPendingAnchor();
      this.refreshFastScrollerGeometry();
    });
  }

  private setupToolbarObserver(): void {
    const toolbar = this.toolbarRef?.nativeElement;
    if (!toolbar) {
      this.toolbarHeightPx.set(0);
      return;
    }

    const updateToolbarHeight = () => {
      this.toolbarHeightPx.set(
        Math.ceil(toolbar.getBoundingClientRect().height),
      );
    };

    this.toolbarResizeObserver = new ResizeObserver(() =>
      updateToolbarHeight(),
    );
    this.toolbarResizeObserver.observe(toolbar);
    updateToolbarHeight();
  }

  trackVirtualRow = (index: number): string => this.getRowIdByIndex(index);

  isSeparatorRow(rowIndex: number): boolean {
    return false;
  }

  getRowHeightPx(rowIndex: number): number {
    return this.rowItemHeightPx();
  }

  onQueryChange(word: string): void {
    const cleanWord = word.startsWith("-") ? word.substring(1) : word;
    this.tagQuery$.next(escapeTagName(cleanWord));
  }

  onSelection(tag: DamebooruTagDto): void {
    const value = this.currentSearchValue();
    const parts = value.split(/\s+/);
    const lastPart = parts[parts.length - 1] || "";
    const prefix = lastPart.startsWith("-") ? "-" : "";

    parts[parts.length - 1] = prefix + escapeTagName(tag.name);
    const newValue = parts.join(" ").trim() + " ";

    this.currentSearchValue.set(newValue);
    this.tagQuery$.next("");
  }

  onSearch(q: string): void {
    const nextQuery = q.trim();
    this.router.navigate([], {
      queryParams: {
        query: nextQuery.length > 0 ? nextQuery : null,
        page: 1,
        offset: 0,
      },
      queryParamsHandling: "merge",
      replaceUrl: true,
    });
  }

  onDensitySelect(nextDensity: GridDensity): void {
    if (nextDensity === this.density()) {
      return;
    }

    this.density.set(nextDensity);
    this.storage.setItem(STORAGE_KEYS.POSTS_GRID_DENSITY, nextDensity);
    this.rowCellsCache.clear();
    this.recalculateLayout(true);
  }

  isDensitySelected(value: GridDensity): boolean {
    return this.density() === value;
  }

  getPageStatus(pageNumber: number): PageStatus {
    return this.pageCache().get(pageNumber)?.status ?? "idle";
  }

  getRowCells(rowIndex: number): GridCell[] {
    if (rowIndex < 0) {
      return [];
    }

    const rowId = this.getRowIdByIndex(rowIndex);

    const cached = this.rowCellsCache.get(rowId);
    if (cached) {
      return cached;
    }

    const cells: GridCell[] = [];
    const columns = Math.max(1, this.columns());
    const rowStartOffset = rowIndex * columns;
    const totalCount = this.totalCount();

    for (let col = 0; col < columns; col += 1) {
      const absoluteOffset = rowStartOffset + col;
      const shouldHaveContent =
        totalCount === null || absoluteOffset < totalCount;

      if (!shouldHaveContent) {
        cells.push({
          kind: "placeholder",
          post: null,
          trackKey: `placeholder-${rowId}-${col}`,
        });
        continue;
      }

      const page = this.offsetToPage(absoluteOffset);
      const pageBaseOffset = (page - 1) * POSTS_PAGE_SIZE;
      const indexInPage = absoluteOffset - pageBaseOffset;
      const entry = this.pageCache().get(page);

      if (entry?.status === "ready") {
        const post = entry.items[indexInPage] ?? null;
        if (post) {
          cells.push({
            kind: "post",
            post,
            trackKey: `post-${post.id}`,
          });
          continue;
        }
      }

      if (entry?.status !== "error") {
        cells.push({
          kind: "skeleton",
          post: null,
          trackKey: `skeleton-${rowId}-${col}`,
        });
        continue;
      }

      cells.push({
        kind: "placeholder",
        post: null,
        trackKey: `placeholder-${rowId}-${col}`,
      });
    }

    this.rowCellsCache.set(rowId, cells);
    return cells;
  }

  retryPage(pageNumber: number): void {
    this.pageCacheStore.retry(pageNumber);
  }

  getThumbnailUrl(post: import("@models").DamebooruPostDto): string {
    return this.damebooru.getThumbnailUrl(
      post.thumbnailLibraryId,
      post.thumbnailContentHash,
    );
  }

  private findPostById(id: number): import("@models").DamebooruPostDto | null {
    for (const page of this.pageCache().values()) {
      if (page.status === "ready") {
        const match = page.items.find((p) => p.id === id);
        if (match) return match;
      }
    }
    return null;
  }

  private handleViewportMouseOver(event: MouseEvent): void {
    if (!this.hoverPreviewEnabled()) {
      return;
    }

    const target = event.target as HTMLElement | null;
    const tile = target?.closest(".post-tile");
    if (!tile) return;

    const idStr = tile.getAttribute("data-post-id");
    if (idStr) {
      const postId = parseInt(idStr, 10);
      if (!Number.isNaN(postId)) {
        const post = this.findPostById(postId);
        if (post) {
          this.clearHoverPreviewTimer();
          this.hoverPreviewTimer = setTimeout(() => {
            this.zone.run(() => this.previewPost.set(post));
          }, this.hoverPreviewDelayMs());
        }
      }
    }
  }

  private handleViewportMouseOut(event: MouseEvent): void {
    this.clearHoverPreviewTimer();
    if (this.previewPost() !== null) {
      const related = event.relatedTarget as Element | null;
      if (related?.closest("[data-preview-card]")) {
        return;
      }
      this.zone.run(() => this.previewPost.set(null));
    }
  }

  @HostListener("document:keydown.escape")
  dismissPreview(): void {
    this.clearHoverPreviewTimer();
    this.previewPost.set(null);
  }

  onFastScrollerPointerDown(event: PointerEvent): void {
    this.fastScroller.onPointerDown(event);
  }

  onFastScrollerPointerMove(event: PointerEvent): void {
    this.fastScroller.onPointerMove(event);
  }

  onFastScrollerPointerUp(event: PointerEvent): void {
    this.fastScroller.onPointerUp(event);
  }

  onFastScrollerPointerCancel(event: PointerEvent): void {
    this.fastScroller.onPointerCancel(event);
  }

  private handleRouteState(state: RouteState): void {
    if (state.query !== this.activeQuery()) {
      this.resetForQuery(state);
      this.routeAnchorAppliedForQuery = state.query;
      return;
    }

    if (this.routeAnchorAppliedForQuery === state.query) {
      return;
    }

    this.routeAnchorAppliedForQuery = state.query;

    const targetOffset = this.resolveAnchorOffset(state.page, state.offset);
    const targetPage = this.offsetToPage(targetOffset);

    if (this.totalCount() === null && !this.pageCache().has(targetPage)) {
      this.pendingAnchorOffset = targetOffset;
      this.anchorPageHint.set(targetPage);
      this.ensurePageLoaded(targetPage);
      this.prefetchAroundPages(new Set([targetPage]));
      this.tryApplyPendingAnchor();
      return;
    }

    if (Math.abs(targetOffset - this.latestMeasuredOffset) <= this.columns()) {
      return;
    }

    this.pendingAnchorOffset = targetOffset;
    this.anchorPageHint.set(this.offsetToPage(targetOffset));
    this.ensurePageLoaded(targetPage);
    this.prefetchAroundPages(new Set([targetPage]));
    this.tryApplyPendingAnchor();
  }

  private resetForQuery(state: RouteState): void {
    this.activeQuery.set(state.query);
    this.currentSearchValue.set(state.query);

    this.rowCellsCache.clear();
    this.pageCacheStore.reset(state.query);
    this.fastScroller.visible.set(false);
    this.fastScroller.dragging.set(false);
    this.toolbarHidden.set(false);
    this.toolbarScrollAccumulator = 0;
    this.toolbarToggleLockUntilMs = 0;

    const targetOffset = this.resolveAnchorOffset(state.page, state.offset);
    this.pendingAnchorOffset = targetOffset;
    this.latestMeasuredOffset = targetOffset;
    this.currentOffset.set(targetOffset);

    const targetPage = offsetToPage(targetOffset, POSTS_PAGE_SIZE);
    this.anchorPageHint.set(targetPage);
    this.fastScrollerBubblePage.set(targetPage);
    this.pageCacheStore.setCurrentPageHint(targetPage);

    this.ensurePageLoaded(targetPage, true);
    this.prefetchAroundPages(new Set([targetPage]));

    this.recalculateLayout(false);
  }

  private setupHotkeys(): void {
    this.hotkeys
      .on("ArrowLeft")
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.stepPage(-1));

    this.hotkeys
      .on("ArrowRight")
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.stepPage(1));
  }

  private stepPage(delta: number): void {
    const totalPages = this.totalPages();
    if (totalPages <= 0) {
      return;
    }

    const targetPage = this.clamp(this.currentPage() + delta, 1, totalPages);
    const targetOffset = (targetPage - 1) * POSTS_PAGE_SIZE;

    this.ensurePageLoaded(targetPage);
    this.prefetchAroundPages(new Set([targetPage]));
    this.scrollToAbsoluteOffset(targetOffset, true);
  }

  private handleRenderedRange(start: number, end: number): void {
    if (this.fastScrollerDragging()) {
      return;
    }

    const rowCount = this.virtualRowCount();
    if (rowCount <= 0) {
      return;
    }

    const startIndex = this.clamp(start, 0, rowCount - 1);
    const endIndex = this.clamp(end - 1, 0, rowCount - 1);

    const pagesInView = new Set<number>();
    for (let index = startIndex; index <= endIndex; index += 1) {
      const rowOffset = index * Math.max(1, this.columns());
      pagesInView.add(this.offsetToPage(rowOffset));
    }

    this.prefetchAroundPages(pagesInView);
  }

  private loadVisibleRange(): void {
    const viewport = this.viewportRef;
    if (!viewport) {
      return;
    }

    const range = viewport.getRenderedRange();
    this.handleRenderedRange(range.start, range.end);
  }

  private onViewportScrolled(): void {
    if (this.fastScrollerDragging()) {
      return;
    }

    this.markScrollingActive();
    this.fastScroller.onScrollActivity();
    this.updateScrollDerivedState();
    this.dismissPreview();
  }

  private updateScrollDerivedState(): void {
    const viewport = this.viewportRef;
    if (!viewport) {
      return;
    }

    const scrollTop = viewport.measureScrollOffset("top");
    const rowIndex = this.getRowIndexForScrollTop(scrollTop);

    const absoluteOffset = this.getFirstVisiblePostOffsetFromRowIndex(rowIndex);
    const visiblePage = this.getPageForRowIndex(rowIndex);
    this.pageCacheStore.setCurrentPageHint(visiblePage);

    this.latestMeasuredOffset = absoluteOffset;

    const isDragging = this.fastScrollerDragging();
    if (isDragging) {
      if (visiblePage !== this.offsetToPage(this.currentOffset())) {
        this.currentOffset.set(absoluteOffset);
      }
    } else if (absoluteOffset !== this.currentOffset()) {
      this.currentOffset.set(absoluteOffset);
    }

    this.fastScrollerBubblePage.set(visiblePage);
    this.updateToolbarVisibility(scrollTop);

    this.refreshFastScrollerGeometry();

    if (!isDragging && !this.isScrolling() && this.totalCount() !== null) {
      this.scheduleUrlSync();
    }
  }

  private ensurePageLoaded(pageNumber: number, force = false): void {
    this.pageCacheStore.setCurrentPageHint(this.currentPage());
    this.pageCacheStore.ensure(pageNumber, force);
  }

  private prefetchAroundPages(basePages: Set<number>): void {
    this.pageCacheStore.setCurrentPageHint(this.currentPage());
    this.pageCacheStore.prefetch(basePages);
  }

  private tryApplyPendingAnchor(): void {
    if (this.pendingAnchorOffset === null) {
      return;
    }

    if (!this.viewportRef) {
      return;
    }

    const totalCount = this.totalCount();
    if (totalCount === null) {
      return;
    }

    if (totalCount <= 0) {
      this.pendingAnchorOffset = null;
      this.currentOffset.set(0);
      this.latestMeasuredOffset = 0;
      return;
    }

    const clampedOffset = this.clamp(
      this.pendingAnchorOffset,
      0,
      totalCount - 1,
    );
    this.pendingAnchorOffset = null;

    this.scrollToAbsoluteOffset(clampedOffset, false);
  }

  private scrollToAbsoluteOffset(offset: number, smooth: boolean): void {
    const viewport = this.viewportRef;
    if (!viewport) {
      return;
    }

    const totalCount = this.totalCount();
    const maxOffset =
      totalCount === null ? offset : Math.max(0, totalCount - 1);
    const clampedOffset = this.clamp(offset, 0, maxOffset);

    const rowIndex = this.getRowIndexForOffset(clampedOffset);
    viewport.scrollToIndex(rowIndex, smooth ? "smooth" : "auto");

    this.latestMeasuredOffset = clampedOffset;
    this.currentOffset.set(clampedOffset);
    this.fastScrollerBubblePage.set(this.offsetToPage(clampedOffset));

    this.refreshFastScrollerGeometry();
    this.scheduleUrlSync();
  }

  private recalculateLayout(preserveCurrentOffset: boolean): void {
    const shell = this.viewportShellRef?.nativeElement;
    if (!shell) {
      return;
    }

    const anchorOffset = preserveCurrentOffset
      ? this.latestMeasuredOffset
      : null;

    const top = shell.getBoundingClientRect().top;
    const availableHeight = Math.floor(
      window.innerHeight - top - PostsComponent.VIEWPORT_BOTTOM_GUTTER_PX,
    );
    this.viewportHeightPx.set(
      Math.max(PostsComponent.MIN_VIEWPORT_HEIGHT_PX, availableHeight),
    );
    const mobile = window.innerWidth <= PostsComponent.MOBILE_BREAKPOINT_PX;
    this.isMobileViewport.set(mobile);
    if (!mobile && this.toolbarHidden()) {
      this.toolbarHidden.set(false);
      this.toolbarScrollAccumulator = 0;
      this.toolbarToggleLockUntilMs = 0;
    }

    const viewportElement = this.viewportRef?.elementRef.nativeElement ?? shell;
    const viewportStyles = window.getComputedStyle(viewportElement);
    const horizontalPadding =
      this.parseCssPixels(viewportStyles.paddingLeft) +
      this.parseCssPixels(viewportStyles.paddingRight);
    const width = viewportElement.clientWidth - horizontalPadding;
    if (width <= 0) {
      return;
    }

    const columns = mobile
      ? this.getMobileColumnsForDensity(width, this.density())
      : this.getResponsiveColumnsForDensity(width, this.density());
    const tileSize = Math.max(
      PostsComponent.MIN_TILE_SIZE_PX,
      (width - (columns - 1) * PostsComponent.GRID_GAP_PX) / columns,
    );

    const columnsChanged = columns !== this.columns();
    const tileSizeChanged = tileSize !== this.tileSizePx();

    if (columnsChanged) {
      this.columns.set(columns);
      this.rowCellsCache.clear();
    }

    if (tileSizeChanged) {
      this.tileSizePx.set(tileSize);
    }

    queueMicrotask(() => {
      this.viewportRef?.checkViewportSize();
      this.refreshFastScrollerGeometry();

      if (anchorOffset !== null && this.totalCount() !== null) {
        this.scrollToAbsoluteOffset(anchorOffset, false);
      }

      this.tryApplyPendingAnchor();
    });
  }

  private updateToolbarVisibility(scrollTop: number): void {
    if (!this.isMobileViewport()) {
      this.lastToolbarScrollTop = scrollTop;
      this.toolbarScrollAccumulator = 0;
      return;
    }

    const normalizedTop = Math.max(0, scrollTop);
    if (normalizedTop <= PostsComponent.TOOLBAR_SHOW_TOP_THRESHOLD_PX) {
      this.lastToolbarScrollTop = normalizedTop;
      this.toolbarScrollAccumulator = 0;
      this.setToolbarHidden(false);
      return;
    }

    if (performance.now() < this.toolbarToggleLockUntilMs) {
      this.lastToolbarScrollTop = normalizedTop;
      this.toolbarScrollAccumulator = 0;
      return;
    }

    const delta = normalizedTop - this.lastToolbarScrollTop;
    this.lastToolbarScrollTop = normalizedTop;
    if (Math.abs(delta) < 0.5) {
      return;
    }

    if (
      this.toolbarScrollAccumulator === 0 ||
      Math.sign(delta) === Math.sign(this.toolbarScrollAccumulator)
    ) {
      this.toolbarScrollAccumulator += delta;
    } else {
      this.toolbarScrollAccumulator = delta;
    }

    if (
      !this.toolbarHidden() &&
      this.toolbarScrollAccumulator >=
      PostsComponent.TOOLBAR_HIDE_SCROLL_DELTA_PX
    ) {
      this.toolbarScrollAccumulator = 0;
      this.setToolbarHidden(true);
      return;
    }

    if (
      this.toolbarHidden() &&
      this.toolbarScrollAccumulator <=
      -PostsComponent.TOOLBAR_SHOW_SCROLL_DELTA_PX
    ) {
      this.toolbarScrollAccumulator = 0;
      this.setToolbarHidden(false);
    }
  }

  private setToolbarHidden(hidden: boolean): void {
    if (this.toolbarHidden() === hidden) {
      return;
    }

    this.toolbarHidden.set(hidden);
    this.toolbarScrollAccumulator = 0;
    this.lastToolbarScrollTop = Math.max(
      0,
      this.viewportRef?.measureScrollOffset("top") ?? this.lastToolbarScrollTop,
    );
    this.toolbarToggleLockUntilMs =
      performance.now() + PostsComponent.TOOLBAR_TOGGLE_GUARD_MS;
  }

  private refreshFastScrollerGeometry(): void {
    this.fastScroller.refreshGeometry();
  }

  private getFastScrollerViewportMetrics(): {
    viewportHeight: number;
    railHeight: number;
    contentHeight: number;
    scrollTop: number;
  } | null {
    const viewport = this.viewportRef;
    const rail = this.fastScrollerRailRef?.nativeElement;
    if (!viewport || !rail) {
      return null;
    }

    const viewportHeight = viewport.getViewportSize();
    const railHeight = rail.clientHeight;
    const contentHeight = this.virtualRowCount() * this.rowItemHeightPx();
    const scrollTop = viewport.measureScrollOffset("top");

    if (viewportHeight <= 0 || railHeight <= 0 || contentHeight <= 0) {
      return null;
    }

    return {
      viewportHeight,
      railHeight,
      contentHeight,
      scrollTop,
    };
  }

  private resolvePageForScrollTop(scrollTop: number): number {
    const rowIndex = this.getRowIndexForScrollTop(scrollTop);
    return this.getPageForRowIndex(rowIndex);
  }

  private resolveOffsetForScrollTop(scrollTop: number): number {
    const rowIndex = this.getRowIndexForScrollTop(scrollTop);
    return this.getFirstVisiblePostOffsetFromRowIndex(rowIndex);
  }

  private handleFastScrollerDragSample(offset: number, page: number): void {
    this.latestMeasuredOffset = offset;
    if (offset !== this.currentOffset()) {
      this.currentOffset.set(offset);
    }
    if (page !== this.fastScrollerBubblePage()) {
      this.fastScrollerBubblePage.set(page);
    }
    this.pageCacheStore.setCurrentPageHint(page);
  }

  private onFastScrollerDragCommit(): void {
    this.markScrollingActive();
    this.loadVisibleRange();
    this.updateScrollDerivedState();
    this.scheduleUrlSync();
  }

  private markScrollingActive(): void {
    this.isScrolling.set(true);
    this.clearScrollIdleTimer();

    this.scrollIdleTimer = setTimeout(() => {
      this.scrollIdleTimer = null;
      if (!this.fastScrollerDragging()) {
        this.isScrolling.set(false);
        if (this.totalCount() !== null) {
          this.scheduleUrlSync();
        }
      }
    }, PostsComponent.SCROLL_IDLE_RESET_MS);
  }

  private clearScrollIdleTimer(): void {
    if (this.scrollIdleTimer !== null) {
      clearTimeout(this.scrollIdleTimer);
      this.scrollIdleTimer = null;
    }
  }

  private scheduleUrlSync(): void {
    this.clearUrlSyncTimer();

    this.urlSyncTimer = setTimeout(() => {
      this.urlSyncTimer = null;
      this.syncUrlToCurrentOffset();
    }, PostsComponent.URL_SYNC_DEBOUNCE_MS);
  }

  private clearUrlSyncTimer(): void {
    if (this.urlSyncTimer !== null) {
      clearTimeout(this.urlSyncTimer);
      this.urlSyncTimer = null;
    }
  }

  private syncUrlToCurrentOffset(): void {
    const totalCount = this.totalCount();
    if (totalCount === null || totalCount <= 0) {
      return;
    }

    const offset = this.clamp(this.latestMeasuredOffset, 0, totalCount - 1);
    const page = this.offsetToPage(offset);
    const query = this.activeQuery();

    const currentQuery = this.query() ?? "";
    const currentOffset = this.parseNonNegativeInt(this.offset()) ?? 0;
    const currentPage =
      this.parsePositiveInt(this.page()) ?? this.offsetToPage(currentOffset);

    if (
      query === currentQuery &&
      offset === currentOffset &&
      page === currentPage
    ) {
      return;
    }

    this.router.navigate([], {
      queryParams: {
        query: query.length > 0 ? query : null,
        page,
        offset,
      },
      queryParamsHandling: "merge",
      replaceUrl: true,
    });
  }

  private parseCssPixels(value: string | null | undefined): number {
    if (!value) {
      return 0;
    }

    const parsed = Number.parseFloat(value);
    if (!Number.isFinite(parsed)) {
      return 0;
    }

    return parsed;
  }

  private getInitialDensity(): GridDensity {
    const stored = this.storage.getItem(STORAGE_KEYS.POSTS_GRID_DENSITY);
    if (stored === "compact" || stored === "comfortable" || stored === "cozy") {
      return stored;
    }

    return "comfortable";
  }

  private getDensityTarget(density: GridDensity): number {
    const preset = this.densityOptions.find(
      (option) => option.value === density,
    );
    return preset?.targetPx ?? 150;
  }

  private getResponsiveColumnsForDensity(
    width: number,
    density: GridDensity,
  ): number {
    const densityTarget = this.getDensityTarget(density);
    return Math.max(
      1,
      Math.floor(
        (width + PostsComponent.GRID_GAP_PX) /
        (densityTarget + PostsComponent.GRID_GAP_PX),
      ),
    );
  }

  private getMobileColumnsForDensity(
    width: number,
    density: GridDensity,
  ): number {
    const desiredColumns =
      density === "compact" ? 4 : density === "comfortable" ? 3 : 2;

    const maxColumnsForMinTile = Math.max(
      1,
      Math.floor(
        (width + PostsComponent.GRID_GAP_PX) /
        (PostsComponent.MIN_TILE_SIZE_PX + PostsComponent.GRID_GAP_PX),
      ),
    );

    return this.clamp(desiredColumns, 1, maxColumnsForMinTile);
  }

  private resolveAnchorOffset(
    page: number | null,
    offset: number | null,
  ): number {
    if (page !== null) {
      const pageBaseOffset = (page - 1) * POSTS_PAGE_SIZE;
      if (offset !== null) {
        const inPageOffset = this.clamp(
          offset - pageBaseOffset,
          0,
          POSTS_PAGE_SIZE - 1,
        );
        return pageBaseOffset + inPageOffset;
      }

      return pageBaseOffset;
    }

    if (offset !== null) {
      return offset;
    }

    return 0;
  }

  private getPageForRowIndex(rowIndex: number): number {
    const columns = Math.max(1, this.columns());
    return this.offsetToPage(rowIndex * columns);
  }

  private getFirstVisiblePostOffsetFromRowIndex(rowIndex: number): number {
    const columns = Math.max(1, this.columns());
    const totalCount = this.totalCount();
    const offset = Math.max(0, rowIndex) * columns;
    if (totalCount === null || totalCount <= 0) {
      return offset;
    }

    return this.clamp(offset, 0, totalCount - 1);
  }

  private getRowIndexForOffset(offset: number): number {
    const columns = Math.max(1, this.columns());
    const normalized = Math.max(0, offset);
    return Math.floor(normalized / columns);
  }

  private getRowIndexForScrollTop(scrollTop: number): number {
    const rowHeight = Math.max(1, this.rowItemHeightPx());
    return Math.max(0, Math.floor(Math.max(0, scrollTop) / rowHeight));
  }

  private getRowIdByIndex(rowIndex: number): string {
    return `r${rowIndex}`;
  }

  private offsetToPage(offset: number): number {
    return offsetToPage(offset, POSTS_PAGE_SIZE);
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

  private parseNonNegativeInt(value: string | null | undefined): number | null {
    if (!value) {
      return null;
    }

    const parsed = Number(value);
    if (!Number.isInteger(parsed) || parsed < 0) {
      return null;
    }

    return parsed;
  }

  private clampAnchorsToTotal(): void {
    const totalCount = this.totalCount();
    if (totalCount === null || totalCount <= 0) {
      return;
    }

    const maxOffset = totalCount - 1;

    if (this.pendingAnchorOffset !== null) {
      this.pendingAnchorOffset = this.clamp(
        this.pendingAnchorOffset,
        0,
        maxOffset,
      );
    }

    if (this.currentOffset() > maxOffset) {
      this.currentOffset.set(maxOffset);
    }

    if (this.latestMeasuredOffset > maxOffset) {
      this.latestMeasuredOffset = maxOffset;
    }
  }

  private clamp(value: number, min: number, max: number): number {
    return Math.min(max, Math.max(min, value));
  }

  readonly pageSize = POSTS_PAGE_SIZE;
  readonly gridGapPx = PostsComponent.GRID_GAP_PX;

  private clearHoverPreviewTimer(): void {
    if (this.hoverPreviewTimer !== null) {
      clearTimeout(this.hoverPreviewTimer);
      this.hoverPreviewTimer = null;
    }
  }
}
