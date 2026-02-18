import { ChangeDetectionStrategy, Component, computed, signal } from '@angular/core';

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

  readonly zoomLevel = signal(1);
  readonly panX = signal(0);
  readonly panY = signal(0);

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

  resetZoom(): void {
    this.zoomLevel.set(1);
    this.panX.set(0);
    this.panY.set(0);
  }

  onWheel(event: WheelEvent): void {
    event.preventDefault();
    const delta = event.deltaY > 0 ? -0.15 : 0.15;
    const currentZoom = this.zoomLevel();
    const newZoom = Math.min(this.maxZoom, Math.max(this.minZoom, currentZoom + delta * currentZoom));

    if (newZoom <= 1) {
      this.resetZoom();
      return;
    }

    // Zoom toward cursor position
    const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
    const cursorX = event.clientX - rect.left - rect.width / 2;
    const cursorY = event.clientY - rect.top - rect.height / 2;
    const scaleFactor = newZoom / currentZoom;

    this.panX.update(px => cursorX - scaleFactor * (cursorX - px));
    this.panY.update(py => cursorY - scaleFactor * (cursorY - py));
    this.zoomLevel.set(newZoom);
  }

  onMouseDown(event: MouseEvent): void {
    if (this.zoomLevel() <= 1) return;
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
    this.panX.set(this.dragStartPanX + (event.clientX - this.dragStartX));
    this.panY.set(this.dragStartPanY + (event.clientY - this.dragStartY));
  }

  onMouseUp(): void {
    this.isDragging = false;
  }
}
