import { Injectable, signal } from '@angular/core';

interface FastScrollerDragMetrics {
    thumbHeight: number;
    maxScrollTop: number;
}

interface PostsFastScrollerBindings {
    getRailElement: () => HTMLElement | null;
    getTotalItems: () => number;
    getDragMetrics: () => FastScrollerDragMetrics | null;
    scrollToOffset: (scrollTop: number) => void;
    resolveOffsetForScrollTop: (scrollTop: number) => number;
    onDragSample: (offset: number) => void;
    onDragCommit: () => void;
}

@Injectable()
export class PostsFastScrollerController {
    private static readonly FAST_SCROLLER_HIDE_DELAY_MS = 1250;
    private static readonly DRAG_SAMPLE_MIN_INTERVAL_MS = 33;

    private bindings: PostsFastScrollerBindings | null = null;

    private fastScrollerHideTimer: ReturnType<typeof setTimeout> | null = null;
    private fastScrollerRafId: number | null = null;
    private pendingFastScrollerClientY: number | null = null;
    private fastScrollerPointerId: number | null = null;
    private dragRailRect: DOMRect | null = null;
    private lastDragSampleTs = 0;
    private lastDragSampleOffset = -1;

    readonly visible = signal(false);
    readonly scrollControlsVisible = signal(true);
    readonly dragging = signal(false);

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
        if (!bindings || !rail || !bindings.getDragMetrics() || bindings.getTotalItems() <= 1) {
            return;
        }

        event.preventDefault();
        this.fastScrollerPointerId = event.pointerId;
        this.dragging.set(true);
        this.reveal();

        this.dragRailRect = rail.getBoundingClientRect();
        this.lastDragSampleTs = 0;
        this.lastDragSampleOffset = -1;

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
        const railRect = this.dragRailRect;
        const metrics = bindings?.getDragMetrics() ?? null;
        if (!bindings || !railRect || !metrics) {
            return;
        }

        const usableHeight = Math.max(1, railRect.height - metrics.thumbHeight);
        const ratio = this.clamp((clientY - railRect.top - metrics.thumbHeight / 2) / usableHeight, 0, 1);
        const targetScrollTop = ratio * metrics.maxScrollTop;
        bindings.scrollToOffset(targetScrollTop);

        const targetOffset = bindings.resolveOffsetForScrollTop(targetScrollTop);
        const now = performance.now();
        if (
            targetOffset !== this.lastDragSampleOffset
            || now - this.lastDragSampleTs >= PostsFastScrollerController.DRAG_SAMPLE_MIN_INTERVAL_MS
        ) {
            this.lastDragSampleTs = now;
            this.lastDragSampleOffset = targetOffset;
            bindings.onDragSample(targetOffset);
        }

        this.reveal();
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
        this.scrollControlsVisible.set(true);
        this.clearFastScrollerHideTimer();

        const totalItems = this.bindings?.getTotalItems() ?? 0;
        if (totalItems > 1) {
            this.visible.set(true);
        }

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
            this.scrollControlsVisible.set(false);
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
}
