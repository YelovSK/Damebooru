import { ChangeDetectionStrategy, Component, ElementRef, computed, input, signal, viewChild } from '@angular/core';

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
  private readonly container = viewChild<ElementRef<HTMLElement>>('container');

  readonly zoomLevel = signal(1);
  readonly panX = signal(0);
  readonly panY = signal(0);

  readonly zoomDelta = input<number>(0.15);
  readonly touchEnabled = input(false);

  isDragging = false;
  private dragStartX = 0;
  private dragStartY = 0;
  private dragStartPanX = 0;
  private dragStartPanY = 0;
  private readonly touchPointers = new Map<number, { x: number; y: number }>();
  private pinchStartDistance = 0;
  private pinchStartZoom = 1;
  private pinchContentX = 0;
  private pinchContentY = 0;

  readonly transform = computed(() => {
    const scale = this.zoomLevel();
    const tx = this.panX();
    const ty = this.panY();
    if (scale === 1 && tx === 0 && ty === 0) return 'none';
    return `translate(${tx}px, ${ty}px) scale(${scale})`;
  });

  readonly isZoomed = computed(() => this.zoomLevel() > 1);

  onDoubleClick(): void {
    const isDefault = this.zoomLevel() === 1 && this.panX() === 0 && this.panY() === 0;
    const newZoom = isDefault ? 2 : 1;
    this.zoomLevel.set(newZoom);
    this.panX.set(0);
    this.panY.set(0);
  }

  resetZoom(): void {
    this.zoomLevel.set(1);
    this.panX.set(0);
    this.panY.set(0);
  }

  onWheel(event: WheelEvent): void {
    event.preventDefault();
    const delta = event.deltaY > 0 ? -this.zoomDelta() : this.zoomDelta();
    const currentZoom = this.zoomLevel();
    const newZoom = Math.min(this.maxZoom, Math.max(this.minZoom, currentZoom + delta * currentZoom));

    // Zoom toward cursor position
    const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
    const cursorX = event.clientX - rect.left - rect.width / 2;
    const cursorY = event.clientY - rect.top - rect.height / 2;
    const scaleFactor = newZoom / currentZoom;

    const nextPanX = cursorX - scaleFactor * (cursorX - this.panX());
    const nextPanY = cursorY - scaleFactor * (cursorY - this.panY());
    const clamped = this.clampPan(nextPanX, nextPanY, newZoom);

    this.panX.set(clamped.x);
    this.panY.set(clamped.y);
    this.zoomLevel.set(newZoom);
  }

  onMouseDown(event: MouseEvent): void {
    if (event.button !== 0) return;

    event.preventDefault();
    this.isDragging = true;
    this.dragStartX = event.clientX;
    this.dragStartY = event.clientY;
    this.dragStartPanX = this.panX();
    this.dragStartPanY = this.panY();
  }

  onMouseMove(event: MouseEvent): void {
    if (!this.isDragging) return;
    const clamped = this.clampPan(
      this.dragStartPanX + (event.clientX - this.dragStartX),
      this.dragStartPanY + (event.clientY - this.dragStartY),
      this.zoomLevel(),
    );

    this.panX.set(clamped.x);
    this.panY.set(clamped.y);
  }

  onMouseUp(): void {
    this.isDragging = false;
  }

  onPointerDown(event: PointerEvent): void {
    if (!this.touchEnabled() || event.pointerType === 'mouse') {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    this.touchPointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
    if (event.currentTarget instanceof HTMLElement) {
      event.currentTarget.setPointerCapture(event.pointerId);
    }

    if (this.touchPointers.size === 1) {
      this.dragStartX = event.clientX;
      this.dragStartY = event.clientY;
      this.dragStartPanX = this.panX();
      this.dragStartPanY = this.panY();
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
    this.panX.set(clamped.x);
    this.panY.set(clamped.y);
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
    }
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

    this.zoomLevel.set(nextZoom);
    this.panX.set(clamped.x);
    this.panY.set(clamped.y);
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
