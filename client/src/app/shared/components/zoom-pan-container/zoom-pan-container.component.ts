import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  type ElementRef,
  NgZone,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { fromEvent } from 'rxjs';
import { VelocityTracker } from '@shared/utils/velocity-tracker';

export interface ZoomPanViewport {
  zoomLevel: number;
  panX: number;
  panY: number;
}

@Component({
  selector: 'app-zoom-pan-container',
  standalone: true,
  templateUrl: './zoom-pan-container.component.html',
  host: { '[style.display]': "'contents'" },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ZoomPanContainerComponent {
  private readonly minZoom = 1;
  private readonly maxZoom = 10;
  private readonly minMomentumVelocity = 0.02;
  private readonly momentumFriction = 0.004;
  private readonly zoomAnimationDuration = 140;
  private readonly zoomAnimationEasing = 'cubic-bezier(0.33, 1, 0.68, 1)';
  private readonly momentumKeyframeCount = 30;
  private readonly destroyRef = inject(DestroyRef);
  private readonly zone = inject(NgZone);
  private readonly container =
    viewChild.required<ElementRef<HTMLElement>>('container');
  private readonly content = viewChild<ElementRef<HTMLElement>>('content');
  private readonly panVelocity = new VelocityTracker(0.65);
  private animation: Animation | null = null;

  private readonly targetViewport = signal<ZoomPanViewport>({
    zoomLevel: 1,
    panX: 0,
    panY: 0,
  });
  private renderedViewport: ZoomPanViewport = {
    zoomLevel: 1,
    panX: 0,
    panY: 0,
  };

  readonly zoomDelta = input<number>(0.225);
  readonly smoothZoomEnabled = input(true);
  readonly momentumEnabled = input(true);
  readonly touchEnabled = input(false);
  readonly doubleClickZoomEnabled = input(true);
  readonly viewport = input<ZoomPanViewport | null>(null);
  readonly viewportChange = output<ZoomPanViewport>();

  readonly zoomLevel = computed(() => this.targetViewport().zoomLevel);
  readonly panX = computed(() => this.targetViewport().panX);
  readonly panY = computed(() => this.targetViewport().panY);

  readonly isDragging = signal(false);
  private dragStartX = 0;
  private dragStartY = 0;
  private dragStartPanX = 0;
  private dragStartPanY = 0;
  private readonly touchPointers = new Map<number, { x: number; y: number }>();
  private pinchStartDistance = 0;
  private pinchStartZoom = 1;
  private pinchContentX = 0;
  private pinchContentY = 0;
  private applyingExternalViewport = false;

  constructor() {
    effect(() => {
      const viewport = this.viewport();
      if (!viewport) {
        return;
      }

      if (this.isSameViewport(viewport, this.targetViewport())) {
        return;
      }

      this.applyingExternalViewport = true;
      this.setTargetViewport(viewport, this.shouldSmoothExternalViewport(viewport) ? 'smooth' : 'instant');
      this.applyingExternalViewport = false;
    });

    afterNextRender(() => {
      this.render(this.renderedViewport);
      this.zone.runOutsideAngular(() =>
        this.listen(this.container().nativeElement),
      );
    });

    this.destroyRef.onDestroy(() => this.cancelAnimation());
  }

  private listen(el: HTMLElement): void {
    const on = <E extends Event>(
      type: string,
      handler: (event: E) => void,
      options?: AddEventListenerOptions,
    ) =>
      fromEvent<E>(el, type, options ?? {})
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe(handler);

    on<WheelEvent>('wheel', (e) => this.onWheel(e), { passive: false });
    on<MouseEvent>('mousedown', (e) => this.onMouseDown(e));
    on<MouseEvent>('mousemove', (e) => this.onMouseMove(e));
    on<MouseEvent>('mouseup', () => this.onMouseUp());
    on<MouseEvent>('mouseleave', () => this.onMouseUp());
    on<MouseEvent>('dblclick', (e) => this.onDoubleClick(e));
    on<PointerEvent>('pointerdown', (e) => this.onPointerDown(e), {
      passive: false,
    });
    on<PointerEvent>('pointermove', (e) => this.onPointerMove(e), {
      passive: false,
    });
    on<PointerEvent>('pointerup', (e) => this.onPointerUp(e));
    on<PointerEvent>('pointercancel', (e) => this.onPointerUp(e));
  }

  private render(viewport: ZoomPanViewport): void {
    this.renderedViewport = viewport;
    const { zoomLevel: scale, panX: tx, panY: ty } = viewport;
    const el = this.content()?.nativeElement;
    if (!el) return;
    el.style.transform =
      scale === 1 && tx === 0 && ty === 0 ? 'none' : this.toTransform(viewport);
  }

  private toTransform({ zoomLevel, panX, panY }: ZoomPanViewport): string {
    return `translate(${panX}px, ${panY}px) scale(${zoomLevel})`;
  }

  private onDoubleClick(event: MouseEvent): void {
    if (!this.doubleClickZoomEnabled()) {
      event.preventDefault();
      event.stopPropagation();
      return;
    }

    const isDefault = this.zoomLevel() === 1 && this.panX() === 0 && this.panY() === 0;
    const newZoom = isDefault ? 2 : 1;
    this.setZoomViewport(newZoom, 0, 0);
  }

  resetZoom(): void {
    this.setZoomViewport(1, 0, 0);
  }

  private onWheel(event: WheelEvent): void {
    event.preventDefault();
    const delta = event.deltaY > 0 ? -this.zoomDelta() : this.zoomDelta();
    const baseViewport = this.targetViewport();
    const currentZoom = baseViewport.zoomLevel;
    const newZoom = Math.min(this.maxZoom, Math.max(this.minZoom, currentZoom + delta * currentZoom));

    // Zoom toward cursor position
    const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
    const cursorX = event.clientX - rect.left - rect.width / 2;
    const cursorY = event.clientY - rect.top - rect.height / 2;
    const scaleFactor = newZoom / currentZoom;

    const nextPanX = cursorX - scaleFactor * (cursorX - baseViewport.panX);
    const nextPanY = cursorY - scaleFactor * (cursorY - baseViewport.panY);
    const clamped = this.clampPan(nextPanX, nextPanY, newZoom);

    this.setZoomViewport(newZoom, clamped.x, clamped.y);
  }

  private onMouseDown(event: MouseEvent): void {
    if (event.button !== 0) return;

    event.preventDefault();
    this.interruptAnimation();
    this.isDragging.set(true);
    this.dragStartX = event.clientX;
    this.dragStartY = event.clientY;
    this.dragStartPanX = this.panX();
    this.dragStartPanY = this.panY();
    this.startVelocityTracking(event.clientX, event.clientY);
  }

  private onMouseMove(event: MouseEvent): void {
    if (!this.isDragging()) return;
    const clamped = this.clampPan(
      this.dragStartPanX + (event.clientX - this.dragStartX),
      this.dragStartPanY + (event.clientY - this.dragStartY),
      this.zoomLevel(),
    );

    this.setViewport(this.zoomLevel(), clamped.x, clamped.y);
    this.trackVelocity(event.clientX, event.clientY);
  }

  private onMouseUp(): void {
    if (this.isDragging()) {
      this.startMomentum();
    }

    this.isDragging.set(false);
  }

  private onPointerDown(event: PointerEvent): void {
    if (!this.touchEnabled() || event.pointerType === 'mouse') {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    this.interruptAnimation();
    this.touchPointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
    if (event.currentTarget instanceof HTMLElement) {
      event.currentTarget.setPointerCapture(event.pointerId);
    }

    if (this.touchPointers.size === 1) {
      this.dragStartX = event.clientX;
      this.dragStartY = event.clientY;
      this.dragStartPanX = this.panX();
      this.dragStartPanY = this.panY();
      this.startVelocityTracking(event.clientX, event.clientY);
      return;
    }

    this.startPinch();
  }

  private onPointerMove(event: PointerEvent): void {
    if (!this.touchEnabled() || !this.touchPointers.has(event.pointerId)) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    this.touchPointers.set(event.pointerId, { x: event.clientX, y: event.clientY });

    if (this.touchPointers.size >= 2) {
      this.updatePinch();
      return;
    }

    if (this.zoomLevel() <= 1) {
      return;
    }

    const clamped = this.clampPan(
      this.dragStartPanX + event.clientX - this.dragStartX,
      this.dragStartPanY + event.clientY - this.dragStartY,
      this.zoomLevel(),
    );
    this.setViewport(this.zoomLevel(), clamped.x, clamped.y);
    this.trackVelocity(event.clientX, event.clientY);
  }

  private onPointerUp(event: PointerEvent): void {
    if (!this.touchEnabled() || !this.touchPointers.has(event.pointerId)) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    this.touchPointers.delete(event.pointerId);
    if (event.currentTarget instanceof HTMLElement && event.currentTarget.hasPointerCapture(event.pointerId)) {
      event.currentTarget.releasePointerCapture(event.pointerId);
    }

    const remaining = Array.from(this.touchPointers.values())[0];
    if (remaining) {
      this.dragStartX = remaining.x;
      this.dragStartY = remaining.y;
      this.dragStartPanX = this.panX();
      this.dragStartPanY = this.panY();
      this.startVelocityTracking(remaining.x, remaining.y);
      return;
    }

    this.startMomentum();
  }

  private clampPan(x: number, y: number, zoom: number): { x: number; y: number } {
    if (zoom <= 1) {
      return { x: 0, y: 0 };
    }

    const containerRect =
      this.container().nativeElement.getBoundingClientRect();

    const contentSize = this.getContainedContentSize(containerRect);
    const maxX = Math.max(0, (contentSize.width * zoom - containerRect.width) / 2);
    const maxY = Math.max(0, (contentSize.height * zoom - containerRect.height) / 2);

    return {
      x: Math.max(-maxX, Math.min(maxX, x)),
      y: Math.max(-maxY, Math.min(maxY, y)),
    };
  }

  private startPinch(): void {
    this.interruptAnimation();
    const points = Array.from(this.touchPointers.values()).slice(0, 2);
    const midpoint = this.getMidpoint(points[0], points[1]);
    this.pinchStartDistance = Math.max(1, this.getDistance(points[0], points[1]));
    this.pinchStartZoom = this.zoomLevel();
    this.pinchContentX = (midpoint.x - this.panX()) / this.pinchStartZoom;
    this.pinchContentY = (midpoint.y - this.panY()) / this.pinchStartZoom;
  }

  private updatePinch(): void {
    const points = Array.from(this.touchPointers.values()).slice(0, 2);
    const midpoint = this.getMidpoint(points[0], points[1]);
    const distance = this.getDistance(points[0], points[1]);
    const nextZoom = Math.min(
      this.maxZoom,
      Math.max(this.minZoom, this.pinchStartZoom * distance / this.pinchStartDistance),
    );
    const clamped = this.clampPan(
      midpoint.x - this.pinchContentX * nextZoom,
      midpoint.y - this.pinchContentY * nextZoom,
      nextZoom,
    );

    this.setViewport(nextZoom, clamped.x, clamped.y);
  }

  private startVelocityTracking(x: number, y: number): void {
    this.panVelocity.reset({ x, y });
  }

  private trackVelocity(x: number, y: number): void {
    this.panVelocity.sample({ x, y });
  }

  private startMomentum(): void {
    if (!this.momentumEnabled() || this.zoomLevel() <= 1 || this.touchPointers.size > 0) {
      return;
    }

    const { x: velocityX, y: velocityY } = this.panVelocity.velocity;
    const peakVelocity = Math.max(Math.abs(velocityX), Math.abs(velocityY));
    if (peakVelocity < this.minMomentumVelocity) {
      return;
    }

    // Velocity decays as v0 * e^(-friction * t) until it drops below the minimum.
    const duration =
      Math.log(peakVelocity / this.minMomentumVelocity) / this.momentumFriction;
    const { zoomLevel, panX, panY } = this.targetViewport();
    const frames = Array.from(
      { length: this.momentumKeyframeCount + 1 },
      (_, i) => {
        const travel =
          (1 -
            Math.exp(
              (-this.momentumFriction * duration * i) /
                this.momentumKeyframeCount,
            )) /
          this.momentumFriction;
        const clamped = this.clampPan(
          panX + velocityX * travel,
          panY + velocityY * travel,
          zoomLevel,
        );
        return { zoomLevel, panX: clamped.x, panY: clamped.y };
      },
    );

    this.setTargetViewport(frames[frames.length - 1], 'instant');
    this.animate(frames, duration, 'linear');
  }

  private setZoomViewport(zoomLevel: number, panX: number, panY: number): void {
    this.setTargetViewport(
      { zoomLevel, panX, panY },
      this.smoothZoomEnabled() ? 'smooth' : 'instant',
    );
  }

  private animate(
    frames: ZoomPanViewport[],
    duration: number,
    easing: string,
  ): void {
    const el = this.content()?.nativeElement;
    if (!el) return;

    const animation = this.zone.runOutsideAngular(() =>
      el.animate(
        frames.map((frame) => ({ transform: this.toTransform(frame) })),
        { duration, easing },
      ),
    );
    animation.onfinish = () => {
      if (this.animation === animation) {
        this.animation = null;
      }
    };
    this.animation = animation;
  }

  private cancelAnimation(): void {
    this.animation?.cancel();
    this.animation = null;
  }

  // The inline style already holds the animation's end state, so stopping mid-flight
  // has to read where the animation currently is and commit that.
  private interruptAnimation(): void {
    if (!this.animation) {
      return;
    }

    this.setTargetViewport(this.getRenderedViewport(), 'instant');
  }

  private getRenderedViewport(): ZoomPanViewport {
    const el = this.content()?.nativeElement;
    if (!this.animation || !el) {
      return this.renderedViewport;
    }

    const matrix = new DOMMatrixReadOnly(getComputedStyle(el).transform);
    return { zoomLevel: matrix.a, panX: matrix.e, panY: matrix.f };
  }

  private setViewport(zoomLevel: number, panX: number, panY: number): void {
    this.setTargetViewport({ zoomLevel, panX, panY }, 'instant');
  }

  private setTargetViewport(viewport: ZoomPanViewport, mode: 'instant' | 'smooth'): void {
    const next = this.normalizeViewport(viewport);
    this.targetViewport.set(next);

    if (!this.applyingExternalViewport) {
      this.viewportChange.emit(next);
    }

    const from = this.getRenderedViewport();
    this.cancelAnimation();
    this.render(next);
    if (mode === 'smooth') {
      this.animate(
        [from, next],
        this.zoomAnimationDuration,
        this.zoomAnimationEasing,
      );
    }
  }

  private clampZoom(zoomLevel: number): number {
    return Math.min(this.maxZoom, Math.max(this.minZoom, zoomLevel));
  }

  private normalizeViewport(viewport: ZoomPanViewport): ZoomPanViewport {
    return {
      zoomLevel: this.clampZoom(viewport.zoomLevel),
      panX: viewport.panX,
      panY: viewport.panY,
    };
  }

  private shouldSmoothExternalViewport(viewport: ZoomPanViewport): boolean {
    return this.smoothZoomEnabled() && viewport.zoomLevel !== this.targetViewport().zoomLevel;
  }

  private isSameViewport(left: ZoomPanViewport, right: ZoomPanViewport): boolean {
    return left.zoomLevel === right.zoomLevel && left.panX === right.panX && left.panY === right.panY;
  }

  private getMidpoint(left: { x: number; y: number }, right: { x: number; y: number }): { x: number; y: number } {
    const rect = this.container().nativeElement.getBoundingClientRect();
    const centerX = rect.left + rect.width / 2;
    const centerY = rect.top + rect.height / 2;
    return {
      x: (left.x + right.x) / 2 - centerX,
      y: (left.y + right.y) / 2 - centerY,
    };
  }

  private getDistance(left: { x: number; y: number }, right: { x: number; y: number }): number {
    return Math.hypot(right.x - left.x, right.y - left.y);
  }

  private getContainedContentSize(containerRect: DOMRect): { width: number; height: number } {
    const media = this.container().nativeElement.querySelector('img, video');
    const intrinsic = this.getIntrinsicSize(media);
    if (!intrinsic) {
      return { width: containerRect.width, height: containerRect.height };
    }

    const fitScale = Math.min(
      containerRect.width / intrinsic.width,
      containerRect.height / intrinsic.height,
    );

    return {
      width: intrinsic.width * fitScale,
      height: intrinsic.height * fitScale,
    };
  }

  private getIntrinsicSize(media: Element | null): { width: number; height: number } | null {
    if (media instanceof HTMLImageElement) {
      const width = media.naturalWidth || Number(media.getAttribute('width'));
      const height = media.naturalHeight || Number(media.getAttribute('height'));
      return width > 0 && height > 0 ? { width, height } : null;
    }

    if (media instanceof HTMLVideoElement) {
      const width = media.videoWidth;
      const height = media.videoHeight;
      return width > 0 && height > 0 ? { width, height } : null;
    }

    return null;
  }
}
