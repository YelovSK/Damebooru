import { ChangeDetectionStrategy, Component, DestroyRef, type ElementRef, computed, effect, inject, input, output, signal, viewChild } from '@angular/core';
import { ValueAnimator, easeOutCubic, lerpNumber } from '@shared/utils/animation';
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
  private readonly destroyRef = inject(DestroyRef);
  private readonly container = viewChild<ElementRef<HTMLElement>>('container');
  private readonly panVelocity = new VelocityTracker(0.65);
  private readonly viewportAnimator = new ValueAnimator<ZoomPanViewport>({
    easing: easeOutCubic,
    interpolate: (from, to, progress) => this.interpolateViewport(from, to, progress),
    onUpdate: (viewport) => this.renderedViewport.set(viewport),
  });

  private readonly targetViewport = signal<ZoomPanViewport>({ zoomLevel: 1, panX: 0, panY: 0 });
  private readonly renderedViewport = signal<ZoomPanViewport>({ zoomLevel: 1, panX: 0, panY: 0 });

  readonly zoomDelta = input<number>(0.15);
  readonly smoothZoomEnabled = input(true);
  readonly momentumEnabled = input(true);
  readonly touchEnabled = input(false);
  readonly doubleClickZoomEnabled = input(true);
  readonly viewport = input<ZoomPanViewport | null>(null);
  readonly viewportChange = output<ZoomPanViewport>();

  readonly zoomLevel = computed(() => this.targetViewport().zoomLevel);
  readonly panX = computed(() => this.targetViewport().panX);
  readonly panY = computed(() => this.targetViewport().panY);

  isDragging = false;
  private dragStartX = 0;
  private dragStartY = 0;
  private dragStartPanX = 0;
  private dragStartPanY = 0;
  private momentumVelocityX = 0;
  private momentumVelocityY = 0;
  private momentumFrameId: number | null = null;
  private lastMomentumTime = 0;
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
      this.cancelMomentum();
      this.cancelViewportAnimation();
      this.setTargetViewport(viewport, this.shouldSmoothExternalViewport(viewport) ? 'smooth' : 'instant');
      this.applyingExternalViewport = false;
    });

    this.destroyRef.onDestroy(() => {
      this.cancelMomentum();
      this.cancelViewportAnimation();
    });
  }

  readonly transform = computed(() => {
    const { zoomLevel: scale, panX: tx, panY: ty } = this.renderedViewport();
    if (scale === 1 && tx === 0 && ty === 0) return 'none';
    return `translate(${tx}px, ${ty}px) scale(${scale})`;
  });

  readonly isZoomed = computed(() => this.zoomLevel() > 1);

  onDoubleClick(event: MouseEvent): void {
    if (!this.doubleClickZoomEnabled()) {
      event.preventDefault();
      event.stopPropagation();
      return;
    }

    this.cancelMomentum();
    const isDefault = this.zoomLevel() === 1 && this.panX() === 0 && this.panY() === 0;
    const newZoom = isDefault ? 2 : 1;
    this.setZoomViewport(newZoom, 0, 0);
  }

  resetZoom(): void {
    this.cancelMomentum();
    this.setZoomViewport(1, 0, 0);
  }

  onWheel(event: WheelEvent): void {
    event.preventDefault();
    this.cancelMomentum();
    const delta = event.deltaY > 0 ? -this.zoomDelta() : this.zoomDelta();
    const baseViewport = this.getZoomBaseViewport();
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

  onMouseDown(event: MouseEvent): void {
    if (event.button !== 0) return;

    event.preventDefault();
    this.cancelMomentum();
    this.interruptViewportAnimation();
    this.isDragging = true;
    this.dragStartX = event.clientX;
    this.dragStartY = event.clientY;
    this.dragStartPanX = this.panX();
    this.dragStartPanY = this.panY();
    this.startVelocityTracking(event.clientX, event.clientY);
  }

  onMouseMove(event: MouseEvent): void {
    if (!this.isDragging) return;
    const clamped = this.clampPan(
      this.dragStartPanX + (event.clientX - this.dragStartX),
      this.dragStartPanY + (event.clientY - this.dragStartY),
      this.zoomLevel(),
    );

    this.setViewport(this.zoomLevel(), clamped.x, clamped.y);
    this.trackVelocity(event.clientX, event.clientY);
  }

  onMouseUp(): void {
    if (this.isDragging) {
      this.startMomentum();
    }

    this.isDragging = false;
  }

  onPointerDown(event: PointerEvent): void {
    if (!this.touchEnabled() || event.pointerType === 'mouse') {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    this.cancelMomentum();
    this.interruptViewportAnimation();
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

  onPointerMove(event: PointerEvent): void {
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

  onPointerUp(event: PointerEvent): void {
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

    const containerRect = this.container()?.nativeElement.getBoundingClientRect();
    if (!containerRect) {
      return { x, y };
    }

    const contentSize = this.getContainedContentSize(containerRect);
    const maxX = Math.max(0, (contentSize.width * zoom - containerRect.width) / 2);
    const maxY = Math.max(0, (contentSize.height * zoom - containerRect.height) / 2);

    return {
      x: Math.max(-maxX, Math.min(maxX, x)),
      y: Math.max(-maxY, Math.min(maxY, y)),
    };
  }

  private startPinch(): void {
    this.cancelMomentum();
    this.interruptViewportAnimation();
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
      this.cancelMomentum();
      return;
    }

    const velocity = this.panVelocity.velocity;
    if (
      Math.abs(velocity.x) < this.minMomentumVelocity
      && Math.abs(velocity.y) < this.minMomentumVelocity
    ) {
      return;
    }

    this.cancelMomentum();
    this.momentumVelocityX = velocity.x;
    this.momentumVelocityY = velocity.y;
    this.lastMomentumTime = performance.now();
    this.momentumFrameId = requestAnimationFrame((time) => this.stepMomentum(time));
  }

  private stepMomentum(time: number): void {
    const elapsed = Math.min(32, time - this.lastMomentumTime);
    this.lastMomentumTime = time;

    const nextX = this.panX() + this.momentumVelocityX * elapsed;
    const nextY = this.panY() + this.momentumVelocityY * elapsed;
    const clamped = this.clampPan(nextX, nextY, this.zoomLevel());

    if (clamped.x !== nextX) {
      this.momentumVelocityX = 0;
    }

    if (clamped.y !== nextY) {
      this.momentumVelocityY = 0;
    }

    this.setViewport(this.zoomLevel(), clamped.x, clamped.y);

    const friction = Math.exp(-this.momentumFriction * elapsed);
    this.momentumVelocityX *= friction;
    this.momentumVelocityY *= friction;

    if (
      Math.abs(this.momentumVelocityX) < this.minMomentumVelocity
      && Math.abs(this.momentumVelocityY) < this.minMomentumVelocity
    ) {
      this.cancelMomentum();
      return;
    }

    this.momentumFrameId = requestAnimationFrame((nextTime) => this.stepMomentum(nextTime));
  }

  private cancelMomentum(): void {
    if (this.momentumFrameId === null) {
      return;
    }

    cancelAnimationFrame(this.momentumFrameId);
    this.momentumFrameId = null;
    this.momentumVelocityX = 0;
    this.momentumVelocityY = 0;
  }

  private setZoomViewport(zoomLevel: number, panX: number, panY: number): void {
    this.setTargetViewport(
      { zoomLevel, panX, panY },
      this.smoothZoomEnabled() ? 'smooth' : 'instant',
    );
  }

  private getZoomBaseViewport(): ZoomPanViewport {
    return this.viewportAnimator.isAnimating && this.viewportAnimator.target
      ? this.viewportAnimator.target
      : this.targetViewport();
  }

  private animateViewport(to: ZoomPanViewport, duration: number): void {
    this.viewportAnimator.animate(this.renderedViewport(), to, duration);
  }

  private cancelViewportAnimation(): void {
    this.viewportAnimator.cancel();
  }

  private interruptViewportAnimation(): void {
    if (!this.viewportAnimator.isAnimating) {
      return;
    }

    const rendered = this.renderedViewport();
    this.cancelViewportAnimation();
    this.setTargetViewport(rendered, 'instant');
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

    if (mode === 'smooth') {
      this.animateViewport(next, this.zoomAnimationDuration);
      return;
    }

    this.cancelViewportAnimation();
    this.renderedViewport.set(next);
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

  private interpolateViewport(from: ZoomPanViewport, to: ZoomPanViewport, progress: number): ZoomPanViewport {
    return {
      zoomLevel: lerpNumber(from.zoomLevel, to.zoomLevel, progress),
      panX: lerpNumber(from.panX, to.panX, progress),
      panY: lerpNumber(from.panY, to.panY, progress),
    };
  }

  private getMidpoint(left: { x: number; y: number }, right: { x: number; y: number }): { x: number; y: number } {
    const rect = this.container()?.nativeElement.getBoundingClientRect();
    const centerX = (rect?.left ?? 0) + (rect?.width ?? 0) / 2;
    const centerY = (rect?.top ?? 0) + (rect?.height ?? 0) / 2;
    return {
      x: (left.x + right.x) / 2 - centerX,
      y: (left.y + right.y) / 2 - centerY,
    };
  }

  private getDistance(left: { x: number; y: number }, right: { x: number; y: number }): number {
    return Math.hypot(right.x - left.x, right.y - left.y);
  }

  private getContainedContentSize(containerRect: DOMRect): { width: number; height: number } {
    const media = this.container()?.nativeElement.querySelector('img, video') ?? null;
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
