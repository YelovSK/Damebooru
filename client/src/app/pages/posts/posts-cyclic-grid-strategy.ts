import { ListRange } from '@angular/cdk/collections';
import { CdkVirtualScrollViewport, VirtualScrollStrategy } from '@angular/cdk/scrolling';
import { Observable, Subject } from 'rxjs';

import { getRowIndexForScrollTop, getRowTopPxForIndex, getVirtualContentHeightPx } from './posts-row-math';

export interface PostsCyclicGridStrategyConfig {
    postRowHeightPx: number;
    separatorRowHeightPx: number;
    rowCount: number;
    totalCount: number | null;
    pageSize: number;
    columns: number;
    anchorPageHint: number;
    minBufferRows: number;
    maxBufferRows: number;
}

export class PostsCyclicGridStrategy implements VirtualScrollStrategy {
    private readonly scrolledIndexChangeSubject = new Subject<number>();

    private viewport: CdkVirtualScrollViewport | null = null;

    private postRowHeightPx = 1;
    private separatorRowHeightPx = 1;
    private rowCount = 0;
    private totalCount: number | null = null;
    private pageSize = 100;
    private columns = 1;
    private anchorPageHint = 1;
    private minBufferRows = 12;
    private maxBufferRows = 24;

    readonly scrolledIndexChange: Observable<number> = this.scrolledIndexChangeSubject.asObservable();

    updateConfig(config: Partial<PostsCyclicGridStrategyConfig>): void {
        if (config.postRowHeightPx !== undefined) {
            this.postRowHeightPx = Math.max(1, config.postRowHeightPx);
        }

        if (config.separatorRowHeightPx !== undefined) {
            this.separatorRowHeightPx = Math.max(1, config.separatorRowHeightPx);
        }

        if (config.rowCount !== undefined) {
            this.rowCount = Math.max(0, Math.floor(config.rowCount));
        }

        if (config.totalCount !== undefined) {
            this.totalCount = config.totalCount === null ? null : Math.max(0, Math.floor(config.totalCount));
        }

        if (config.pageSize !== undefined) {
            this.pageSize = Math.max(1, Math.floor(config.pageSize));
        }

        if (config.columns !== undefined) {
            this.columns = Math.max(1, Math.floor(config.columns));
        }

        if (config.anchorPageHint !== undefined) {
            this.anchorPageHint = Math.max(1, Math.floor(config.anchorPageHint));
        }

        if (config.minBufferRows !== undefined) {
            this.minBufferRows = Math.max(0, Math.floor(config.minBufferRows));
        }

        if (config.maxBufferRows !== undefined) {
            this.maxBufferRows = Math.max(this.minBufferRows, Math.floor(config.maxBufferRows));
        }

        this.updateViewport();
    }

    attach(viewport: CdkVirtualScrollViewport): void {
        this.viewport = viewport;
        this.updateViewport();
    }

    detach(): void {
        this.viewport = null;
    }

    onContentScrolled(): void {
        this.updateRenderedRange();
    }

    onDataLengthChanged(): void {
        this.updateViewport();
    }

    onContentRendered(): void {
        // no-op
    }

    onRenderedOffsetChanged(): void {
        // no-op
    }

    scrollToIndex(index: number, behavior: ScrollBehavior): void {
        const viewport = this.viewport;
        if (!viewport) {
            return;
        }

        const clampedIndex = this.clamp(index, 0, Math.max(0, this.rowCount - 1));
        viewport.scrollToOffset(this.getRowTopPx(clampedIndex), behavior);
    }

    private updateViewport(): void {
        const viewport = this.viewport;
        if (!viewport) {
            return;
        }

        viewport.setTotalContentSize(this.getTotalContentHeightPx());
        this.updateRenderedRange();
    }

    private updateRenderedRange(): void {
        const viewport = this.viewport;
        if (!viewport) {
            return;
        }

        if (this.rowCount <= 0) {
            viewport.setRenderedRange({ start: 0, end: 0 });
            viewport.setRenderedContentOffset(0);
            return;
        }

        const viewportSize = viewport.getViewportSize();
        const scrollOffset = viewport.measureScrollOffset('top');
        const firstVisibleIndex = this.getRowIndexForScrollTop(scrollOffset);
        this.scrolledIndexChangeSubject.next(firstVisibleIndex);
        const totalContentHeightPx = this.getTotalContentHeightPx();
        const bufferRowHeightPx = Math.max(this.postRowHeightPx, this.separatorRowHeightPx);
        const startBufferOffsetPx = Math.max(0, scrollOffset - this.minBufferRows * bufferRowHeightPx);
        const endBufferOffsetPx = Math.min(
            totalContentHeightPx,
            scrollOffset + viewportSize + this.maxBufferRows * bufferRowHeightPx
        );

        const start = this.getRowIndexForScrollTop(startBufferOffsetPx);
        const endIndex = this.getRowIndexForScrollTop(Math.max(0, endBufferOffsetPx - 1));
        const end = this.clamp(endIndex + 1, start, this.rowCount);

        const range: ListRange = { start, end };
        const currentRange = viewport.getRenderedRange();
        if (currentRange.start !== range.start || currentRange.end !== range.end) {
            viewport.setRenderedRange(range);
        }

        viewport.setRenderedContentOffset(this.getRowTopPx(start));
    }

    private getTotalContentHeightPx(): number {
        return getVirtualContentHeightPx(
            this.rowCount,
            this.totalCount,
            this.pageSize,
            this.columns,
            this.anchorPageHint,
            this.postRowHeightPx,
            this.separatorRowHeightPx
        );
    }

    private getRowTopPx(rowIndex: number): number {
        return getRowTopPxForIndex(
            rowIndex,
            this.rowCount,
            this.totalCount,
            this.pageSize,
            this.columns,
            this.anchorPageHint,
            this.postRowHeightPx,
            this.separatorRowHeightPx
        );
    }

    private getRowIndexForScrollTop(scrollTopPx: number): number {
        return getRowIndexForScrollTop(
            scrollTopPx,
            this.rowCount,
            this.totalCount,
            this.pageSize,
            this.columns,
            this.anchorPageHint,
            this.postRowHeightPx,
            this.separatorRowHeightPx
        );
    }

    private clamp(value: number, min: number, max: number): number {
        return Math.min(max, Math.max(min, Math.floor(value)));
    }
}
