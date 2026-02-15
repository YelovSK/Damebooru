import {
    AfterViewInit,
    ChangeDetectionStrategy,
    Component,
    DestroyRef,
    ElementRef,
    ViewChild,
    computed,
    effect,
    inject,
    input,
    signal,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { CdkVirtualScrollViewport, ScrollingModule } from '@angular/cdk/scrolling';
import { toObservable, toSignal, takeUntilDestroyed } from '@angular/core/rxjs-interop';
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
} from 'rxjs';

import { BakabooruService } from '@services/api/bakabooru/bakabooru.service';
import { HotkeysService } from '@services/hotkeys.service';
import { environment } from '@env/environment';
import { BakabooruTagDto } from '@models';
import { AutocompleteComponent } from '@shared/components/autocomplete/autocomplete.component';
import { escapeTagName } from '@shared/utils/utils';
import { AppLinks } from '@app/app.paths';
import { StorageService, STORAGE_KEYS } from '@services/storage.service';
import {
    getFirstVisibleOffsetForRowIndex,
    getPageForRowIndex,
    getRowIndexForOffset,
    getRowIndexForScrollTop,
    getVirtualContentHeightPx,
    getVirtualRowCount,
    getVirtualRowPosition,
    offsetToPage,
} from './posts-row-math';
import { VirtualRowIndexDataSource } from './posts-row-index.data-source';
import { PostsPageCacheStore } from './posts-page-cache.store';
import { PostsFastScrollerController } from './posts-fast-scroller.controller';
import { PostsCyclicGridStrategyDirective } from './posts-cyclic-grid-strategy.directive';
import {
    GridCell,
    GridDensity,
    PageStatus,
    PostRow,
    RouteState,
    VirtualRow,
    VirtualRowPosition,
} from './posts.types';

@Component({
    selector: 'app-posts',
    imports: [CommonModule, RouterLink, AutocompleteComponent, ScrollingModule, PostsCyclicGridStrategyDirective],
    providers: [PostsPageCacheStore, PostsFastScrollerController],
    templateUrl: './posts.component.html',
    styleUrl: './posts.component.css',
    changeDetection: ChangeDetectionStrategy.OnPush
})
export class PostsComponent implements AfterViewInit {
    private static readonly PAGE_SIZE = 100;

    private static readonly GRID_GAP_PX = 8;

    private static readonly MIN_VIEWPORT_HEIGHT_PX = 260;
    private static readonly VIEWPORT_BOTTOM_GUTTER_PX = 10;

    private static readonly URL_SYNC_DEBOUNCE_MS = 160;

    private static readonly SCROLL_IDLE_RESET_MS = 120;

    @ViewChild('viewportShell') private viewportShellRef?: ElementRef<HTMLElement>;
    @ViewChild('postsViewport') private viewportRef?: CdkVirtualScrollViewport;
    @ViewChild('fastScrollerRail') private fastScrollerRailRef?: ElementRef<HTMLElement>;

    private gridResizeObserver?: ResizeObserver;

    private readonly rowCellsCache = new Map<string, GridCell[]>();

    private pendingAnchorOffset: number | null = 0;
    private lastInternalRouteUpdate: { query: string; page: number; offset: number } | null = null;
    private internalRouteSyncDeadlineMs = 0;

    private latestMeasuredOffset = 0;

    private urlSyncTimer: ReturnType<typeof setTimeout> | null = null;
    private scrollIdleTimer: ReturnType<typeof setTimeout> | null = null;

    private readonly bakabooru = inject(BakabooruService);
    private readonly router = inject(Router);
    private readonly storage = inject(StorageService);
    private readonly hotkeys = inject(HotkeysService);
    private readonly destroyRef = inject(DestroyRef);
    private readonly pageCacheStore = inject(PostsPageCacheStore);
    private readonly fastScroller = inject(PostsFastScrollerController);

    readonly appLinks = AppLinks;
    readonly environment = environment;

    query = input<string | null>('');
    page = input<string | null>(null);
    offset = input<string | null>('0');

    currentSearchValue = signal('');

    readonly densityOptions: ReadonlyArray<{ value: GridDensity; label: string; targetPx: number }> = [
        { value: 'compact', label: 'Compact', targetPx: 110 },
        { value: 'comfortable', label: 'Comfortable', targetPx: 150 },
        { value: 'cozy', label: 'Cozy', targetPx: 190 },
    ];

    density = signal<GridDensity>(this.getInitialDensity());

    activeQuery = signal('');
    readonly totalCount = this.pageCacheStore.totalCount;
    readonly isInitialLoading = this.pageCacheStore.isInitialLoading;

    readonly pageCache = this.pageCacheStore.pageCache;

    anchorPageHint = signal(1);

    viewportHeightPx = signal(480);
    columns = signal(1);
    tileSizePx = signal(150);

    currentOffset = signal(0);
    isScrolling = signal(false);

    readonly totalPages = computed(() => {
        const total = this.totalCount();
        if (total === null || total <= 0) {
            return 0;
        }

        return Math.ceil(total / PostsComponent.PAGE_SIZE);
    });

    readonly rowItemHeightPx = computed(() => this.tileSizePx() + PostsComponent.GRID_GAP_PX);
    readonly separatorRowHeightPx = computed(() => {
        const postRowHeight = this.rowItemHeightPx();
        return Math.max(28, Math.min(48, Math.round(postRowHeight * 0.38)));
    });
    readonly virtualRowCount = computed(() =>
        getVirtualRowCount(
            this.totalCount(),
            this.anchorPageHint(),
            PostsComponent.PAGE_SIZE,
            this.columns()
        )
    );
    readonly virtualRowDataSource = new VirtualRowIndexDataSource();

    readonly hasNoResults = computed(() => this.totalCount() === 0 && !this.isInitialLoading());

    readonly currentPage = computed(() => offsetToPage(this.currentOffset(), PostsComponent.PAGE_SIZE));

    readonly gridTemplateColumns = computed(() => `repeat(${this.columns()}, minmax(0, 1fr))`);

    readonly fastScrollerVisible = this.fastScroller.visible;
    readonly fastScrollerDragging = this.fastScroller.dragging;
    readonly fastScrollerThumbTopPx = this.fastScroller.thumbTopPx;
    readonly fastScrollerThumbHeightPx = this.fastScroller.thumbHeightPx;
    readonly fastScrollerBubbleTopPx = this.fastScroller.bubbleTopPx;
    readonly fastScrollerBubblePage = this.fastScroller.bubblePage;

    readonly showFastScrollerBubble = computed(() => this.fastScrollerVisible() && this.totalPages() > 1);
    readonly virtualMinBufferRows = computed(() => this.fastScrollerDragging() ? 4 : 12);
    readonly virtualMaxBufferRows = computed(() => this.fastScrollerDragging() ? 8 : 24);

    private tagQuery$ = new Subject<string>();
    tagSuggestions = toSignal(
        this.tagQuery$.pipe(
            switchMap(word => {
                if (word.length < 1) {
                    return of([]);
                }

                return this.bakabooru.getTags(`*${word}* sort:usages`, 0, 15).pipe(
                    map(res => res.results),
                    catchError(() => of([]))
                );
            })
        ),
        { initialValue: [] as BakabooruTagDto[] }
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

        this.fastScroller.configure({
            getRailElement: () => this.fastScrollerRailRef?.nativeElement ?? null,
            getTotalPages: () => this.totalPages(),
            getViewportMetrics: () => this.getFastScrollerViewportMetrics(),
            scrollToOffset: scrollTop => this.viewportRef?.scrollToOffset(scrollTop, 'auto'),
            resolvePageForScrollTop: scrollTop => this.resolvePageForScrollTop(scrollTop),
            resolveOffsetForScrollTop: scrollTop => this.resolveOffsetForScrollTop(scrollTop),
            onDragSample: (offset, page) => this.handleFastScrollerDragSample(offset, page),
            onDragCommit: () => this.onFastScrollerDragCommit(),
        });

        combineLatest([
            toObservable(this.query).pipe(map(value => value ?? '')),
            toObservable(this.page).pipe(map(value => this.parsePositiveInt(value))),
            toObservable(this.offset).pipe(map(value => this.parseNonNegativeInt(value))),
        ])
            .pipe(
                map(([query, page, offset]) => ({ query, page, offset } as RouteState)),
                distinctUntilChanged((left, right) =>
                    left.query === right.query
                    && left.page === right.page
                    && left.offset === right.offset
                ),
                takeUntilDestroyed(this.destroyRef)
            )
            .subscribe(state => this.handleRouteState(state));

        this.destroyRef.onDestroy(() => {
            this.gridResizeObserver?.disconnect();
            this.fastScroller.dispose();
            this.clearUrlSyncTimer();
            this.clearScrollIdleTimer();
            this.virtualRowDataSource.disconnect();
        });

        this.setupHotkeys();
    }

    ngAfterViewInit(): void {
        const shell = this.viewportShellRef?.nativeElement;
        if (!shell) {
            return;
        }

        this.gridResizeObserver = new ResizeObserver(() => this.recalculateLayout(true));
        this.gridResizeObserver.observe(shell);

        fromEvent(window, 'resize')
            .pipe(auditTime(120), takeUntilDestroyed(this.destroyRef))
            .subscribe(() => this.recalculateLayout(true));

        const viewport = this.viewportRef;
        if (viewport) {
            viewport.renderedRangeStream
                .pipe(auditTime(40), takeUntilDestroyed(this.destroyRef))
                .subscribe(range => this.handleRenderedRange(range.start, range.end));

            viewport.elementScrolled()
                .pipe(auditTime(16), takeUntilDestroyed(this.destroyRef))
                .subscribe(() => this.onViewportScrolled());
        }

        queueMicrotask(() => {
            this.recalculateLayout(false);
            this.tryApplyPendingAnchor();
            this.refreshFastScrollerGeometry();
        });
    }

    trackVirtualRow = (index: number): string => this.getRowIdByIndex(index);

    isSeparatorRow(rowIndex: number): boolean {
        return (this.getVirtualRowPosition(rowIndex)?.rowOffsetInPage ?? -1) === 0;
    }

    getRowHeightPx(rowIndex: number): number {
        return this.isSeparatorRow(rowIndex) ? this.separatorRowHeightPx() : this.rowItemHeightPx();
    }

    getVirtualRow(rowIndex: number): VirtualRow | null {
        const position = this.getVirtualRowPosition(rowIndex);
        if (!position) {
            return null;
        }

        const pageBaseOffset = (position.page - 1) * PostsComponent.PAGE_SIZE;
        if (position.rowOffsetInPage === 0) {
            return {
                kind: 'separator',
                page: position.page,
                rowId: this.getSeparatorRowId(position.page),
                startOffset: pageBaseOffset,
            };
        }

        const rowInPage = position.rowOffsetInPage - 1;
        const columns = Math.max(1, this.columns());
        const rowStart = rowInPage * columns;
        const count = Math.min(columns, Math.max(0, position.pageItemCount - rowStart));

        return {
            kind: 'posts',
            page: position.page,
            rowId: this.getPostRowId(position.page, rowInPage),
            rowInPage,
            startOffset: pageBaseOffset + rowStart,
            count,
        };
    }

    onQueryChange(word: string): void {
        const cleanWord = word.startsWith('-') ? word.substring(1) : word;
        this.tagQuery$.next(escapeTagName(cleanWord));
    }

    onSelection(tag: BakabooruTagDto): void {
        const value = this.currentSearchValue();
        const parts = value.split(/\s+/);
        const lastPart = parts[parts.length - 1] || '';
        const prefix = lastPart.startsWith('-') ? '-' : '';

        parts[parts.length - 1] = prefix + escapeTagName(tag.name);
        const newValue = parts.join(' ').trim() + ' ';

        this.currentSearchValue.set(newValue);
        this.tagQuery$.next('');
    }

    onSearch(q: string): void {
        const nextQuery = q.trim();
        this.router.navigate([], {
            queryParams: {
                query: nextQuery.length > 0 ? nextQuery : null,
                page: 1,
                offset: 0,
            },
            queryParamsHandling: 'merge',
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
        return this.pageCache().get(pageNumber)?.status ?? 'idle';
    }

    getRowCells(row: PostRow): GridCell[] {
        const cached = this.rowCellsCache.get(row.rowId);
        if (cached) {
            return cached;
        }

        const cells: GridCell[] = [];
        const columns = this.columns();
        const entry = this.pageCache().get(row.page);

        const startInPage = row.rowInPage * columns;

        for (let col = 0; col < columns; col += 1) {
            const cellIndexInPage = startInPage + col;
            const shouldHaveContent = col < row.count;

            if (entry?.status === 'ready' && shouldHaveContent) {
                const post = entry.items[cellIndexInPage] ?? null;
                if (post) {
                    cells.push({
                        kind: 'post',
                        post,
                        trackKey: `post-${post.id}`,
                    });
                    continue;
                }
            }

            if (shouldHaveContent && entry?.status !== 'error') {
                cells.push({
                    kind: 'skeleton',
                    post: null,
                    trackKey: `skeleton-${row.rowId}-${col}`,
                });
                continue;
            }

            cells.push({
                kind: 'placeholder',
                post: null,
                trackKey: `placeholder-${row.rowId}-${col}`,
            });
        }

        this.rowCellsCache.set(row.rowId, cells);
        return cells;
    }

    retryPage(pageNumber: number): void {
        this.pageCacheStore.retry(pageNumber);
    }

    getMediaType(contentType: string): 'image' | 'animation' | 'video' {
        if (contentType.startsWith('video/')) {
            return 'video';
        }

        if (contentType === 'image/gif') {
            return 'animation';
        }

        return 'image';
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
        if (this.consumeInternalRouteUpdate(state)) {
            return;
        }

        if (state.query !== this.activeQuery()) {
            this.resetForQuery(state);
            return;
        }

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

        const targetOffset = this.resolveAnchorOffset(state.page, state.offset);
        this.pendingAnchorOffset = targetOffset;
        this.latestMeasuredOffset = targetOffset;
        this.currentOffset.set(targetOffset);

        const targetPage = offsetToPage(targetOffset, PostsComponent.PAGE_SIZE);
        this.anchorPageHint.set(targetPage);
        this.fastScrollerBubblePage.set(targetPage);
        this.pageCacheStore.setCurrentPageHint(targetPage);

        this.ensurePageLoaded(targetPage, true);
        this.prefetchAroundPages(new Set([targetPage]));

        this.recalculateLayout(false);
    }

    private consumeInternalRouteUpdate(state: RouteState): boolean {
        if (!this.lastInternalRouteUpdate) {
            return false;
        }

        if (performance.now() > this.internalRouteSyncDeadlineMs) {
            this.lastInternalRouteUpdate = null;
            this.internalRouteSyncDeadlineMs = 0;
            return false;
        }

        if (state.query !== this.lastInternalRouteUpdate.query) {
            this.lastInternalRouteUpdate = null;
            this.internalRouteSyncDeadlineMs = 0;
            return false;
        }

        const fallbackOffset = state.offset ?? 0;
        const effectivePage = state.page ?? this.offsetToPage(fallbackOffset);
        const effectiveOffset = state.offset ?? (effectivePage - 1) * PostsComponent.PAGE_SIZE;

        const matches =
            this.lastInternalRouteUpdate.query === state.query
            && this.lastInternalRouteUpdate.page === effectivePage
            && this.lastInternalRouteUpdate.offset === effectiveOffset;

        if (matches) {
            this.lastInternalRouteUpdate = null;
            this.internalRouteSyncDeadlineMs = 0;
            return true;
        }

        this.lastInternalRouteUpdate = null;
        this.internalRouteSyncDeadlineMs = 0;
        return false;
    }

    private setupHotkeys(): void {
        this.hotkeys.on('ArrowLeft')
            .pipe(takeUntilDestroyed(this.destroyRef))
            .subscribe(() => this.stepPage(-1));

        this.hotkeys.on('ArrowRight')
            .pipe(takeUntilDestroyed(this.destroyRef))
            .subscribe(() => this.stepPage(1));
    }

    private stepPage(delta: number): void {
        const totalPages = this.totalPages();
        if (totalPages <= 0) {
            return;
        }

        const targetPage = this.clamp(this.currentPage() + delta, 1, totalPages);
        const targetOffset = (targetPage - 1) * PostsComponent.PAGE_SIZE;

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
            pagesInView.add(this.getPageForRowIndex(index));
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
    }

    private updateScrollDerivedState(): void {
        const viewport = this.viewportRef;
        if (!viewport) {
            return;
        }

        const scrollTop = viewport.measureScrollOffset('top');
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
        }
        else if (absoluteOffset !== this.currentOffset()) {
            this.currentOffset.set(absoluteOffset);
        }

        this.fastScrollerBubblePage.set(visiblePage);

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

        const clampedOffset = this.clamp(this.pendingAnchorOffset, 0, totalCount - 1);
        this.pendingAnchorOffset = null;

        this.scrollToAbsoluteOffset(clampedOffset, false);
    }

    private scrollToAbsoluteOffset(offset: number, smooth: boolean): void {
        const viewport = this.viewportRef;
        if (!viewport) {
            return;
        }

        const totalCount = this.totalCount();
        const maxOffset = totalCount === null ? offset : Math.max(0, totalCount - 1);
        const clampedOffset = this.clamp(offset, 0, maxOffset);

        const rowIndex = this.getRowIndexForOffset(clampedOffset);
        viewport.scrollToIndex(rowIndex, smooth ? 'smooth' : 'auto');

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

        const anchorOffset = preserveCurrentOffset ? this.latestMeasuredOffset : null;

        const top = shell.getBoundingClientRect().top;
        const availableHeight = Math.floor(window.innerHeight - top - PostsComponent.VIEWPORT_BOTTOM_GUTTER_PX);
        this.viewportHeightPx.set(Math.max(PostsComponent.MIN_VIEWPORT_HEIGHT_PX, availableHeight));

        const viewportElement = this.viewportRef?.elementRef.nativeElement ?? shell;
        const viewportStyles = window.getComputedStyle(viewportElement);
        const horizontalPadding = this.parseCssPixels(viewportStyles.paddingLeft) + this.parseCssPixels(viewportStyles.paddingRight);
        const width = viewportElement.clientWidth - horizontalPadding;
        if (width <= 0) {
            return;
        }

        const densityTarget = this.getDensityTarget(this.density());
        const columns = Math.max(1, Math.floor((width + PostsComponent.GRID_GAP_PX) / (densityTarget + PostsComponent.GRID_GAP_PX)));
        const tileSize = Math.max(56, (width - (columns - 1) * PostsComponent.GRID_GAP_PX) / columns);

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
        const contentHeight = getVirtualContentHeightPx(
            this.virtualRowCount(),
            this.totalCount(),
            PostsComponent.PAGE_SIZE,
            this.columns(),
            this.anchorPageHint(),
            this.rowItemHeightPx(),
            this.separatorRowHeightPx()
        );
        const scrollTop = viewport.measureScrollOffset('top');

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
        this.fastScrollerBubblePage.set(page);
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

        const currentQuery = this.query() ?? '';
        const currentOffset = this.parseNonNegativeInt(this.offset()) ?? 0;
        const currentPage = this.parsePositiveInt(this.page()) ?? this.offsetToPage(currentOffset);

        if (query === currentQuery && offset === currentOffset && page === currentPage) {
            return;
        }

        this.lastInternalRouteUpdate = { query, page, offset };
        this.internalRouteSyncDeadlineMs = performance.now() + 1500;

        this.router.navigate([], {
            queryParams: {
                query: query.length > 0 ? query : null,
                page,
                offset,
            },
            queryParamsHandling: 'merge',
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
        if (stored === 'compact' || stored === 'comfortable' || stored === 'cozy') {
            return stored;
        }

        return 'comfortable';
    }

    private getDensityTarget(density: GridDensity): number {
        const preset = this.densityOptions.find(option => option.value === density);
        return preset?.targetPx ?? 150;
    }

    private resolveAnchorOffset(page: number | null, offset: number | null): number {
        if (page !== null) {
            const pageBaseOffset = (page - 1) * PostsComponent.PAGE_SIZE;
            if (offset !== null) {
                const inPageOffset = this.clamp(offset - pageBaseOffset, 0, PostsComponent.PAGE_SIZE - 1);
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
        return getPageForRowIndex(
            rowIndex,
            this.virtualRowCount(),
            this.totalCount(),
            PostsComponent.PAGE_SIZE,
            this.columns(),
            this.anchorPageHint()
        );
    }

    private getFirstVisiblePostOffsetFromRowIndex(rowIndex: number): number {
        return getFirstVisibleOffsetForRowIndex(
            rowIndex,
            this.virtualRowCount(),
            this.totalCount(),
            PostsComponent.PAGE_SIZE,
            this.columns(),
            this.anchorPageHint()
        );
    }

    private getRowIndexForOffset(offset: number): number {
        return getRowIndexForOffset(
            offset,
            this.virtualRowCount(),
            this.totalCount(),
            PostsComponent.PAGE_SIZE,
            this.columns()
        );
    }

    private getRowIndexForScrollTop(scrollTop: number): number {
        return getRowIndexForScrollTop(
            scrollTop,
            this.virtualRowCount(),
            this.totalCount(),
            PostsComponent.PAGE_SIZE,
            this.columns(),
            this.anchorPageHint(),
            this.rowItemHeightPx(),
            this.separatorRowHeightPx()
        );
    }

    private getRowIdByIndex(rowIndex: number): string {
        const position = this.getVirtualRowPosition(rowIndex);
        if (!position) {
            return `r${rowIndex}`;
        }

        if (position.rowOffsetInPage === 0) {
            return this.getSeparatorRowId(position.page);
        }

        return this.getPostRowId(position.page, position.rowOffsetInPage - 1);
    }

    private getVirtualRowPosition(rowIndex: number): VirtualRowPosition | null {
        return getVirtualRowPosition(
            rowIndex,
            this.virtualRowCount(),
            this.totalCount(),
            PostsComponent.PAGE_SIZE,
            this.columns(),
            this.anchorPageHint()
        );
    }

    private getSeparatorRowId(page: number): string {
        return `p${page}-s`;
    }

    private getPostRowId(page: number, rowInPage: number): string {
        return `p${page}-r${rowInPage}`;
    }

    private offsetToPage(offset: number): number {
        return offsetToPage(offset, PostsComponent.PAGE_SIZE);
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
            this.pendingAnchorOffset = this.clamp(this.pendingAnchorOffset, 0, maxOffset);
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

    readonly pageSize = PostsComponent.PAGE_SIZE;
    readonly gridGapPx = PostsComponent.GRID_GAP_PX;
}
