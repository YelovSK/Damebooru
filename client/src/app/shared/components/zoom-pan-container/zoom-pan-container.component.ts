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

  isDragging = false;
  private dragStartX = 0;
  private dragStartY = 0;
  private dragStartPanX = 0;
  private dragStartPanY = 0;

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
