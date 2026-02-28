import { Injectable, signal } from '@angular/core';

interface FastScrollerViewportMetrics {
    viewportHeight: number;
    railHeight: number;
    contentHeight: number;
    scrollTop: number;
}

interface PostsFastScrollerBindings {
    getRailElement: () => HTMLElement | null;
    getTotalPages: () => number;
    getViewportMetrics: () => FastScrollerViewportMetrics | null;
    scrollToOffset: (scrollTop: number) => void;
    resolvePageForScrollTop: (scrollTop: number) => number;
    resolveOffsetForScrollTop: (scrollTop: number) => number;
    onDragSample: (offset: number, page: number) => void;
    onDragCommit: () => void;
}

@Injectable()
export class PostsFastScrollerController {
    private static readonly FAST_SCROLLER_MIN_THUMB_PX = 44;
    private static readonly FAST_SCROLLER_HIDE_DELAY_MS = 250;
    private static readonly FAST_SCROLLER_BUBBLE_HEIGHT_PX = 36;
    private static readonly DRAG_SAMPLE_MIN_INTERVAL_MS = 33;

    private bindings: PostsFastScrollerBindings | null = null;

    private fastScrollerHideTimer: ReturnType<typeof setTimeout> | null = null;
    private fastScrollerRafId: number | null = null;
    private pendingFastScrollerClientY: number | null = null;
    private fastScrollerPointerId: number | null = null;
    private dragRailRect: DOMRect | null = null;
    private dragThumbHeightPx = PostsFastScrollerController.FAST_SCROLLER_MIN_THUMB_PX;
    private dragUsableHeight = 1;
    private lastDragSampleTs = 0;
    private lastDragSamplePage = -1;

    readonly visible = signal(false);
    readonly dragging = signal(false);
    readonly thumbTopPx = signal(0);
    readonly thumbHeightPx = signal(56);
    readonly bubbleTopPx = signal(0);
    readonly bubblePage = signal(1);

    configure(bindings: PostsFastScrollerBindings): void {
        this.bindings = bindings;
    }

    dispose(): void {
        this.clearFastScrollerHideTimer();
        this.cancelFastScrollerRaf();
    }

    onPointerDown(event: PointerEvent): void {
        if (event.button !== 0 && event.pointerType === 'mouse') {
            return;
        }

        const bindings = this.bindings;
        const rail = bindings?.getRailElement() ?? null;
        const metrics = bindings?.getViewportMetrics() ?? null;
        if (!bindings || !rail || !metrics || bindings.getTotalPages() <= 1) {
            return;
        }

        event.preventDefault();
        this.fastScrollerPointerId = event.pointerId;
        this.dragging.set(true);
        this.reveal();

        this.dragRailRect = rail.getBoundingClientRect();
        this.dragThumbHeightPx = this.calculateThumbHeight(metrics);
        this.dragUsableHeight = Math.max(1, this.dragRailRect.height - this.dragThumbHeightPx);
        this.thumbHeightPx.set(this.dragThumbHeightPx);
        this.lastDragSampleTs = 0;
        this.lastDragSamplePage = -1;

        rail.setPointerCapture(event.pointerId);
        this.pendingFastScrollerClientY = event.clientY;
        this.scheduleFastScrollerFrame();
    }

    onPointerMove(event: PointerEvent): void {
        if (this.fastScrollerPointerId !== event.pointerId) {
            return;
        }

        event.preventDefault();
        this.pendingFastScrollerClientY = event.clientY;
        this.scheduleFastScrollerFrame();
    }

    onPointerUp(event: PointerEvent): void {
        this.finishFastScrollerPointer(event);
    }

    onPointerCancel(event: PointerEvent): void {
        this.finishFastScrollerPointer(event);
    }

    onScrollActivity(): void {
        this.reveal();
    }

    refreshGeometry(): void {
        const bindings = this.bindings;
        const metrics = bindings?.getViewportMetrics() ?? null;
        if (!metrics) {
            this.thumbTopPx.set(0);
            this.thumbHeightPx.set(0);
            this.bubbleTopPx.set(0);
            return;
        }

        const maxScrollTop = Math.max(0, metrics.contentHeight - metrics.viewportHeight);
        let thumbHeight = maxScrollTop <= 0
            ? metrics.railHeight
            : metrics.railHeight * (metrics.viewportHeight / metrics.contentHeight);

        thumbHeight = Math.max(
            PostsFastScrollerController.FAST_SCROLLER_MIN_THUMB_PX,
            Math.min(metrics.railHeight, thumbHeight)
        );

        const thumbTravel = Math.max(0, metrics.railHeight - thumbHeight);
        const thumbTop = maxScrollTop <= 0 ? 0 : (metrics.scrollTop / maxScrollTop) * thumbTravel;

        this.thumbHeightPx.set(thumbHeight);
        this.updateFastScrollerVisual(thumbTop, metrics.railHeight, thumbHeight);
    }

    private scheduleFastScrollerFrame(): void {
        if (this.fastScrollerRafId !== null) {
            return;
        }

        this.fastScrollerRafId = requestAnimationFrame(() => {
            this.fastScrollerRafId = null;

            const clientY = this.pendingFastScrollerClientY;
            this.pendingFastScrollerClientY = null;
            if (clientY === null) {
                return;
            }

            this.applyFastScrollerPointer(clientY);
        });
    }

    private cancelFastScrollerRaf(): void {
        if (this.fastScrollerRafId !== null) {
            cancelAnimationFrame(this.fastScrollerRafId);
            this.fastScrollerRafId = null;
        }

        this.pendingFastScrollerClientY = null;
    }

    private applyFastScrollerPointer(clientY: number): void {
        const bindings = this.bindings;
        const rail = bindings?.getRailElement() ?? null;
        const metrics = bindings?.getViewportMetrics() ?? null;
        if (!bindings || !rail || !metrics) {
            return;
        }

        const railRect = this.dragRailRect ?? rail.getBoundingClientRect();
        const thumbHeight = this.clamp(this.dragThumbHeightPx, 1, Math.max(1, railRect.height));
        const usableHeight = this.dragUsableHeight;
        const clampedCenter = this.clamp(
            clientY,
            railRect.top + thumbHeight / 2,
            railRect.bottom - thumbHeight / 2
        );

        const ratio = (clampedCenter - railRect.top - thumbHeight / 2) / usableHeight;
        const thumbTop = ratio * usableHeight;
        this.updateFastScrollerVisual(thumbTop, railRect.height, thumbHeight);

        const maxScrollTop = Math.max(0, metrics.contentHeight - metrics.viewportHeight);
        const targetScrollTop = ratio * maxScrollTop;
        bindings.scrollToOffset(targetScrollTop);

        const targetPage = bindings.resolvePageForScrollTop(targetScrollTop);
        const targetOffset = bindings.resolveOffsetForScrollTop(targetScrollTop);
        if (targetPage !== this.bubblePage()) {
            this.bubblePage.set(targetPage);
        }

        const now = performance.now();
        if (
            targetPage !== this.lastDragSamplePage
            || now - this.lastDragSampleTs >= PostsFastScrollerController.DRAG_SAMPLE_MIN_INTERVAL_MS
        ) {
            this.lastDragSampleTs = now;
            this.lastDragSamplePage = targetPage;
            bindings.onDragSample(targetOffset, targetPage);
        }

        this.reveal();
    }

    private updateFastScrollerVisual(thumbTop: number, railHeight: number, thumbHeight: number): void {
        const clampedThumbTop = this.clamp(thumbTop, 0, Math.max(0, railHeight - thumbHeight));
        this.thumbTopPx.set(clampedThumbTop);

        const bubbleTop = this.clamp(
            clampedThumbTop + thumbHeight / 2 - PostsFastScrollerController.FAST_SCROLLER_BUBBLE_HEIGHT_PX / 2,
            0,
            Math.max(0, railHeight - PostsFastScrollerController.FAST_SCROLLER_BUBBLE_HEIGHT_PX)
        );

        this.bubbleTopPx.set(bubbleTop);
    }

    private finishFastScrollerPointer(event: PointerEvent): void {
        if (this.fastScrollerPointerId !== event.pointerId) {
            return;
        }

        const rail = this.bindings?.getRailElement() ?? null;
        if (rail && rail.hasPointerCapture(event.pointerId)) {
            rail.releasePointerCapture(event.pointerId);
        }

        this.fastScrollerPointerId = null;
        this.dragRailRect = null;
        this.cancelFastScrollerRaf();
        this.dragging.set(false);

        this.bindings?.onDragCommit();
        this.scheduleFastScrollerHide();
    }

    private reveal(): void {
        const totalPages = this.bindings?.getTotalPages() ?? 0;
        if (totalPages <= 1) {
            return;
        }

        this.visible.set(true);
        this.clearFastScrollerHideTimer();

        if (!this.dragging()) {
            this.scheduleFastScrollerHide();
        }
    }

    private scheduleFastScrollerHide(): void {
        this.clearFastScrollerHideTimer();

        if (this.dragging()) {
            return;
        }

        this.fastScrollerHideTimer = setTimeout(() => {
            this.visible.set(false);
            this.fastScrollerHideTimer = null;
        }, PostsFastScrollerController.FAST_SCROLLER_HIDE_DELAY_MS);
    }

    private clearFastScrollerHideTimer(): void {
        if (this.fastScrollerHideTimer !== null) {
            clearTimeout(this.fastScrollerHideTimer);
            this.fastScrollerHideTimer = null;
        }
    }

    private clamp(value: number, min: number, max: number): number {
        return Math.min(max, Math.max(min, value));
    }

    private calculateThumbHeight(metrics: FastScrollerViewportMetrics): number {
        const maxScrollTop = Math.max(0, metrics.contentHeight - metrics.viewportHeight);
        const thumbHeight = maxScrollTop <= 0
            ? metrics.railHeight
            : metrics.railHeight * (metrics.viewportHeight / metrics.contentHeight);

        return this.clamp(
            thumbHeight,
            PostsFastScrollerController.FAST_SCROLLER_MIN_THUMB_PX,
            Math.max(PostsFastScrollerController.FAST_SCROLLER_MIN_THUMB_PX, metrics.railHeight)
        );
    }
}
