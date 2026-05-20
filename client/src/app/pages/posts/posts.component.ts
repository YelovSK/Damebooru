import {
  type AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  type ElementRef,
  HostListener,
  computed,
  effect,
  inject,
  input,
  signal,
  NgZone,
  viewChild,
} from "@angular/core";
import { CommonModule } from "@angular/common";
import { Router } from "@angular/router";
import {
  type CdkVirtualScrollViewport,
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
import { type DamebooruPostDto, type DamebooruTagDto } from "@models";
import { AutocompleteComponent } from "@shared/components/autocomplete/autocomplete.component";
import { escapeTagName } from "@shared/utils/utils";
import { AppLinks, AppPaths } from "@app/app.paths";
import { StorageService, STORAGE_KEYS } from "@services/storage.service";
import { SettingsService } from "@services/settings.service";
import { PostPreviewOverlayComponent } from "@shared/components/post-preview-overlay/post-preview-overlay.component";
import { PostPreviewHoverGateService } from "@shared/components/post-preview-overlay/post-preview-hover-gate.service";
import { PostTileComponent } from "@shared/components/post-tile/post-tile.component";
import { MobileBottomSheetComponent } from "@shared/components/mobile-bottom-sheet/mobile-bottom-sheet.component";
import { POSTS_CACHE_SEGMENT_SIZE, POSTS_FETCH_SIZE } from "./posts.constants";
import { VirtualRowIndexDataSource } from "./posts-row-index.data-source";
import { PostsRangeCacheStore } from "./posts-range-cache.store";
import { PostsFastScrollerController } from "./posts-fast-scroller.controller";
import {
  type GridCell,
  type GridDensity,
  type RouteState,
} from "./posts.types";

@Component({
  selector: "app-posts",
  imports: [
    CommonModule,
    AutocompleteComponent,
    ScrollingModule,
    PostPreviewOverlayComponent,
    PostTileComponent,
    MobileBottomSheetComponent,
  ],
  providers: [PostsRangeCacheStore, PostsFastScrollerController],
  templateUrl: "./posts.component.html",
  styleUrl: "./posts.component.css",
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PostsComponent implements AfterViewInit {
  private static readonly GRID_GAP_PX = 8;
  private static readonly MOBILE_BREAKPOINT_PX = 768;
  private static readonly MIN_TILE_SIZE_PX = 56;

  private static readonly MIN_VIEWPORT_HEIGHT_PX = 260;
  private static readonly VIEWPORT_BOTTOM_GUTTER_PX = 0;

  private static readonly URL_SYNC_DEBOUNCE_MS = 160;

  private static readonly SCROLL_IDLE_RESET_MS = 120;
  private static readonly HOVER_PREVIEW_DELAY_MS = 700;

  private readonly viewportShellRef = viewChild<ElementRef<HTMLElement>>("viewportShell");
  private readonly viewportRef = viewChild<CdkVirtualScrollViewport>("postsViewport");
  private readonly fastScrollerRailRef = viewChild<ElementRef<HTMLElement>>("fastScrollerRail");

  private gridResizeObserver?: ResizeObserver;

  private readonly rowCellsCache = new Map<string, GridCell[]>();

  private pendingAnchorOffset: number | null = 0;
  private routeAnchorAppliedForQuery: string | null = null;

  private latestMeasuredOffset = 0;

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
  private readonly rangeCacheStore = inject(PostsRangeCacheStore);
  private readonly fastScroller = inject(PostsFastScrollerController);
  private readonly previewHoverGate = inject(PostPreviewHoverGateService);
  private readonly zone = inject(NgZone);

  readonly appLinks = AppLinks;
  readonly appPaths = AppPaths;

  query = input<string | null>("");
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
  readonly totalCount = this.rangeCacheStore.totalCount;
  readonly isInitialLoading = this.rangeCacheStore.isInitialLoading;

  readonly segmentCache = this.rangeCacheStore.segmentCache;
  readonly hoverPreviewEnabled = this.settingsService.enablePostPreviewOnHover;
  readonly hoverPreviewDelayMs = computed(() => {
    const raw = this.settingsService.postPreviewDelayMs();
    if (!Number.isFinite(raw)) {
      return PostsComponent.HOVER_PREVIEW_DELAY_MS;
    }

    return Math.max(0, Math.min(5000, Math.round(raw)));
  });

  viewportHeightPx = signal(480);
  columns = signal(1);
  tileSizePx = signal(150);

  currentOffset = signal(0);
  isScrolling = signal(false);
  previewPost = signal<DamebooruPostDto | null>(null);
  mobileControlsOpen = signal(false);

  readonly mobileControlsLabel = computed(() => {
    const query = this.activeQuery().trim();
    return query.length > 0 ? query : "Posts";
  });

  readonly rowItemHeightPx = computed(
    () => this.tileSizePx() + PostsComponent.GRID_GAP_PX,
  );
  readonly virtualRowCount = computed(() => {
    const columns = Math.max(1, this.columns());
    const total = this.totalCount();
    if (total === null) {
      return Math.max(1, Math.ceil(POSTS_FETCH_SIZE / columns));
    }

    return Math.ceil(Math.max(0, total) / columns);
  });
  readonly virtualRowDataSource = new VirtualRowIndexDataSource();

  readonly hasNoResults = computed(
    () => this.totalCount() === 0 && !this.isInitialLoading(),
  );

  readonly gridTemplateColumns = computed(
    () => `repeat(${this.columns()}, minmax(0, 1fr))`,
  );

  readonly fastScrollerVisible = this.fastScroller.visible;
  readonly scrollControlsVisible = this.fastScroller.scrollControlsVisible;
  readonly fastScrollerDragging = this.fastScroller.dragging;
  readonly fastScrollerThumbTopPx = this.fastScroller.thumbTopPx;
  readonly fastScrollerThumbHeightPx = this.fastScroller.thumbHeightPx;
  readonly fastScrollerBubbleTopPx = this.fastScroller.bubbleTopPx;
  readonly fastScrollerBubblePost = this.fastScroller.bubbleLabel;

  readonly showFastScrollerBubble = computed(
    () => this.fastScrollerVisible() && (this.totalCount() ?? 0) > 1,
  );
  readonly virtualMinBufferRows = 4;
  readonly virtualMaxBufferRows = 8;

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
      this.segmentCache();
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
      getRailElement: () => this.fastScrollerRailRef()?.nativeElement ?? null,
      getTotalItems: () => this.totalCount() ?? 0,
      getViewportMetrics: () => this.getFastScrollerViewportMetrics(),
      scrollToOffset: (scrollTop) =>
        this.viewportRef()?.scrollToOffset(scrollTop, "auto"),
      resolveBubbleLabelForScrollTop: (scrollTop) =>
        this.resolveBubbleLabelForScrollTop(scrollTop),
      resolveOffsetForScrollTop: (scrollTop) =>
        this.resolveOffsetForScrollTop(scrollTop),
      onDragSample: (offset, bubbleLabel) =>
        this.handleFastScrollerDragSample(offset, bubbleLabel),
      onDragCommit: () => this.onFastScrollerDragCommit(),
    });

    combineLatest([
      toObservable(this.query).pipe(map((value) => value ?? "")),
      toObservable(this.offset).pipe(
        map((value) => this.parseNonNegativeInt(value)),
      ),
    ])
      .pipe(
        map(([query, offset]) => ({ query, offset }) as RouteState),
        distinctUntilChanged(
          (left, right) =>
            left.query === right.query &&
            left.offset === right.offset,
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((state) => this.handleRouteState(state));

    this.destroyRef.onDestroy(() => {
      this.gridResizeObserver?.disconnect();
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
    const shell = this.viewportShellRef()?.nativeElement;
    if (!shell) {
      return;
    }

    this.gridResizeObserver = new ResizeObserver(() =>
      this.recalculateLayout(false),
    );
    this.gridResizeObserver.observe(shell);

    fromEvent(window, "resize")
      .pipe(auditTime(120), takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.recalculateLayout(false));

    const viewport = this.viewportRef();
    if (viewport) {
      viewport.renderedRangeStream
        .pipe(auditTime(40), takeUntilDestroyed(this.destroyRef))
        .subscribe((range) => this.handleRenderedRange(range.start, range.end));

      viewport
        .elementScrolled()
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe(() => {
          if (this.fastScrollerDragging()) {
            return;
          }

          if (this.scrollRafId !== null) {
            return;
          }

          this.scrollRafId = requestAnimationFrame(() => {
            this.scrollRafId = null;
            if (this.fastScrollerDragging()) {
              return;
            }
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
        fromEvent<MouseEvent>(el, "mousemove")
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe((e) => this.handleViewportMouseMove(e));
      });
    }

    const rail = this.fastScrollerRailRef()?.nativeElement;
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

  trackVirtualRow = (_index: number, rowIndex: number): number => rowIndex;

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
        page: null,
        offset: 0,
      },
      queryParamsHandling: "merge",
      replaceUrl: true,
    });
    this.closeMobileControls();
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

      const segmentIndex = Math.floor(absoluteOffset / POSTS_CACHE_SEGMENT_SIZE);
      const indexInSegment = absoluteOffset - segmentIndex * POSTS_CACHE_SEGMENT_SIZE;
      const entry = this.segmentCache().get(segmentIndex);

      if (entry?.status === "ready") {
        const post = entry.items[indexInSegment] ?? null;
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

  getThumbnailUrl(post: DamebooruPostDto): string {
    return this.damebooru.getThumbnailUrl(
      post.thumbnailLibraryId,
      post.thumbnailContentHash,
    );
  }

  private findPostById(id: number): DamebooruPostDto | null {
    for (const segment of this.segmentCache().values()) {
      if (segment.status === "ready") {
        const match = segment.items.find((p) => p.id === id);
        if (match) return match;
      }
    }
    return null;
  }

  private handleViewportMouseOver(event: MouseEvent): void {
    if (!this.hoverPreviewEnabled()) {
      return;
    }

    if (this.previewHoverGate.isSuppressed()) {
      return;
    }

    this.schedulePreviewForEventTarget(event.target);
  }

  private handleViewportMouseMove(event: MouseEvent): void {
    if (!this.hoverPreviewEnabled() || !this.previewHoverGate.resumeIfSuppressed()) {
      return;
    }

    this.schedulePreviewForEventTarget(event.target);
  }

  private schedulePreviewForEventTarget(target: EventTarget | null): void {
    const element = target as HTMLElement | null;
    const tile = element?.closest(".post-tile");
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
    if (this.mobileControlsOpen()) {
      this.closeMobileControls();
      return;
    }

    this.dismissHoverPreview();
  }

  closePreview(): void {
    this.dismissHoverPreview();
  }

  private dismissHoverPreview(): void {
    this.clearHoverPreviewTimer();
    this.previewHoverGate.suppressUntilPointerMove();
    this.previewPost.set(null);
  }

  openMobileControls(): void {
    this.mobileControlsOpen.set(true);
  }

  onMobileControlsButtonKeydown(event: KeyboardEvent): void {
    if (event.key !== "Enter" && event.key !== " ") {
      return;
    }

    event.preventDefault();
    this.openMobileControls();
  }

  closeMobileControls(): void {
    this.mobileControlsOpen.set(false);
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

    const targetOffset = this.resolveAnchorOffset(state.offset);

    if (this.totalCount() === null && !this.rangeCacheStore.hasOffset(targetOffset)) {
      this.pendingAnchorOffset = targetOffset;
      this.ensureOffsetLoaded(targetOffset);
      this.tryApplyPendingAnchor();
      return;
    }

    if (Math.abs(targetOffset - this.latestMeasuredOffset) <= this.columns()) {
      return;
    }

    this.pendingAnchorOffset = targetOffset;
    this.ensureOffsetLoaded(targetOffset);
    this.tryApplyPendingAnchor();
  }

  private resetForQuery(state: RouteState): void {
    this.activeQuery.set(state.query);
    this.currentSearchValue.set(state.query);

    this.rowCellsCache.clear();
    this.rangeCacheStore.reset(state.query);
    this.fastScroller.visible.set(false);
    this.fastScroller.dragging.set(false);

    const targetOffset = this.resolveAnchorOffset(state.offset);
    this.pendingAnchorOffset = targetOffset;
    this.latestMeasuredOffset = targetOffset;
    this.currentOffset.set(targetOffset);

    this.fastScrollerBubblePost.set(targetOffset + 1);
    this.rangeCacheStore.setCurrentOffsetHint(targetOffset);

    this.ensureOffsetLoaded(targetOffset, true);

    this.recalculateLayout(false);
  }

  private setupHotkeys(): void {
    this.hotkeys
      .on("ArrowLeft")
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.stepFetchWindow(-1));

    this.hotkeys
      .on("ArrowRight")
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.stepFetchWindow(1));
  }

  private stepFetchWindow(delta: number): void {
    const totalCount = this.totalCount();
    if (totalCount === null || totalCount <= 0) {
      return;
    }

    const targetOffset = this.clamp(
      this.currentOffset() + delta * POSTS_FETCH_SIZE,
      0,
      totalCount - 1,
    );

    this.ensureOffsetLoaded(targetOffset);
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

    const columns = Math.max(1, this.columns());
    const startOffset = startIndex * columns;
    const endOffset = (endIndex + 1) * columns;
    this.ensureWindowLoaded(startOffset, endOffset);
  }

  private loadVisibleRange(): void {
    const viewport = this.viewportRef();
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
    this.dismissHoverPreview();
  }

  private updateScrollDerivedState(): void {
    const viewport = this.viewportRef();
    if (!viewport) {
      return;
    }

    const scrollTop = viewport.measureScrollOffset("top");
    const rowIndex = this.getRowIndexForScrollTop(scrollTop);
    const absoluteOffset = this.getFirstVisiblePostOffsetFromRowIndex(rowIndex);
    this.rangeCacheStore.setCurrentOffsetHint(absoluteOffset);

    this.latestMeasuredOffset = absoluteOffset;

    const isDragging = this.fastScrollerDragging();
    if (isDragging) {
      if (absoluteOffset !== this.currentOffset()) {
        this.currentOffset.set(absoluteOffset);
      }
    } else if (absoluteOffset !== this.currentOffset()) {
      this.currentOffset.set(absoluteOffset);
    }

    this.fastScrollerBubblePost.set(absoluteOffset + 1);

    this.refreshFastScrollerGeometry();

    if (!isDragging && !this.isScrolling() && this.totalCount() !== null) {
      this.scheduleUrlSync();
    }
  }

  private ensureOffsetLoaded(offset: number, force = false): void {
    this.rangeCacheStore.setCurrentOffsetHint(this.currentOffset());
    this.rangeCacheStore.ensureAroundOffset(offset, force);
  }

  private ensureWindowLoaded(startOffset: number, endOffset: number, force = false): void {
    this.rangeCacheStore.setCurrentOffsetHint(this.currentOffset());
    this.rangeCacheStore.ensureWindow(startOffset, endOffset, force);
  }

  private tryApplyPendingAnchor(): void {
    if (this.pendingAnchorOffset === null) {
      return;
    }

    if (!this.viewportRef()) {
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
    const viewport = this.viewportRef();
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
    this.fastScrollerBubblePost.set(clampedOffset + 1);

    this.refreshFastScrollerGeometry();
    this.scheduleUrlSync();
  }

  private recalculateLayout(preserveCurrentOffset: boolean): void {
    const shell = this.viewportShellRef()?.nativeElement;
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

    const viewportElement = this.viewportRef()?.elementRef.nativeElement ?? shell;
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
      this.viewportRef()?.checkViewportSize();
      this.refreshFastScrollerGeometry();

      if (anchorOffset !== null && this.totalCount() !== null) {
        this.scrollToAbsoluteOffset(anchorOffset, false);
      }

      this.tryApplyPendingAnchor();
    });
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
    const viewport = this.viewportRef();
    const rail = this.fastScrollerRailRef()?.nativeElement;
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

  private resolveBubbleLabelForScrollTop(scrollTop: number): number {
    const rowIndex = this.getRowIndexForScrollTop(scrollTop);
    return this.getFirstVisiblePostOffsetFromRowIndex(rowIndex) + 1;
  }

  private resolveOffsetForScrollTop(scrollTop: number): number {
    const rowIndex = this.getRowIndexForScrollTop(scrollTop);
    return this.getFirstVisiblePostOffsetFromRowIndex(rowIndex);
  }

  private handleFastScrollerDragSample(offset: number, bubbleLabel: number): void {
    this.latestMeasuredOffset = offset;
    if (offset !== this.currentOffset()) {
      this.currentOffset.set(offset);
    }
    if (bubbleLabel !== this.fastScrollerBubblePost()) {
      this.fastScrollerBubblePost.set(bubbleLabel);
    }
    this.rangeCacheStore.setCurrentOffsetHint(offset);
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
    const query = this.activeQuery();

    const currentQuery = this.query() ?? "";
    const currentOffset = this.parseNonNegativeInt(this.offset()) ?? 0;

    if (query === currentQuery && offset === currentOffset) {
      return;
    }

    this.router.navigate([], {
      queryParams: {
        query: query.length > 0 ? query : null,
        page: null,
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

  private resolveAnchorOffset(offset: number | null): number {
    if (offset !== null) {
      return offset;
    }

    return 0;
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

  readonly gridGapPx = PostsComponent.GRID_GAP_PX;

  private clearHoverPreviewTimer(): void {
    if (this.hoverPreviewTimer !== null) {
      clearTimeout(this.hoverPreviewTimer);
      this.hoverPreviewTimer = null;
    }
  }
}
