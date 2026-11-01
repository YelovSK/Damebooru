import { Directive, ElementRef, DestroyRef, inject, input } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { fromEvent, merge, timer } from 'rxjs';
import { switchMap, takeUntil, tap } from 'rxjs/operators';

export type TooltipPosition = 'top' | 'bottom' | 'left' | 'right';

const oppositeSide: Record<TooltipPosition, TooltipPosition> = { top: 'bottom', bottom: 'top', left: 'right', right: 'left' };

interface Point { left: number; top: number }

@Directive({
  selector: '[appTooltip]',
  standalone: true,
})
export class TooltipDirective {
  private readonly el = inject(ElementRef<HTMLElement>);
  private readonly destroyRef = inject(DestroyRef);

  /** Tooltip text content */
  appTooltip = input.required<string>();

  /** Tooltip position */
  tooltipPosition = input<TooltipPosition>('top');

  /** Delay before showing tooltip (ms) */
  tooltipDelay = input<number>(200);

  /** Show tooltip near mouse cursor instead of anchoring to host element */
  tooltipFollowCursor = input<boolean>(false);

  private tooltipElement: HTMLElement | null = null;
  private interactionMode: 'mouse' | 'focus' = 'mouse';
  private lastMousePosition: { x: number; y: number } | null = null;
  private readonly hostGapPx = 8;
  private readonly cursorGapPx = 12;
  private readonly viewportPaddingPx = 8;

  constructor() {
    this.setupListeners();
  }

  private setupListeners(): void {
    const el = this.el.nativeElement;

    const mouseEnter$ = fromEvent<MouseEvent>(el, 'mouseenter');
    const mouseMove$ = fromEvent<MouseEvent>(el, 'mousemove');
    const mouseLeave$ = fromEvent(el, 'mouseleave');
    const focus$ = fromEvent(el, 'focusin');
    const blur$ = fromEvent(el, 'focusout');
    const hide$ = merge(mouseLeave$, blur$);

    mouseMove$.pipe(
      tap(event => {
        this.lastMousePosition = { x: event.clientX, y: event.clientY };
      }),
      takeUntilDestroyed(this.destroyRef)
    ).subscribe();

    mouseEnter$.pipe(
      tap(event => {
        this.interactionMode = 'mouse';
        this.lastMousePosition = { x: event.clientX, y: event.clientY };
      }),
      switchMap(() => timer(this.tooltipDelay()).pipe(
        takeUntil(hide$),
        tap(() => this.show())
      )),
      takeUntilDestroyed(this.destroyRef)
    ).subscribe();

    focus$.pipe(
      tap(() => {
        this.interactionMode = 'focus';
      }),
      switchMap(() => timer(this.tooltipDelay()).pipe(
        takeUntil(hide$),
        tap(() => this.show())
      )),
      takeUntilDestroyed(this.destroyRef)
    ).subscribe();

    hide$.pipe(
      tap(() => this.hide()),
      takeUntilDestroyed(this.destroyRef)
    ).subscribe();

    // Cleanup on destroy
    this.destroyRef.onDestroy(() => this.hide());
  }

  private show(): void {
    if (this.tooltipElement || !this.appTooltip()) return;

    this.tooltipElement = document.createElement('div');
    this.tooltipElement.className = 'app-tooltip';
    this.tooltipElement.textContent = this.appTooltip();
    document.body.appendChild(this.tooltipElement);

    this.positionTooltip();

    // Trigger animation on next frame
    requestAnimationFrame(() => {
      this.tooltipElement?.classList.add('app-tooltip-visible');
    });
  }

  private hide(): void {
    if (!this.tooltipElement) return;

    const el = this.tooltipElement;
    el.classList.remove('app-tooltip-visible');
    this.tooltipElement = null;

    // Remove after fade-out animation
    setTimeout(() => el.remove(), 150);
    this.lastMousePosition = null;
  }

  private positionTooltip(): void {
    if (!this.tooltipElement) return;

    const tooltipRect = this.tooltipElement.getBoundingClientRect();
    const cursor = this.lastMousePosition;
    const compute = this.tooltipFollowCursor() && this.interactionMode === 'mouse' && cursor
      ? (side: TooltipPosition) => this.computePositionFromPoint(side, cursor.x, cursor.y, tooltipRect, this.cursorGapPx)
      : (side: TooltipPosition) => this.computePositionFromRect(side, this.el.nativeElement.getBoundingClientRect(), tooltipRect, this.hostGapPx);

    let side = this.tooltipPosition();
    let position = compute(side);
    if (!this.fitsOnSide(side, position, tooltipRect)) {
      const flipped = compute(oppositeSide[side]);
      if (this.fitsOnSide(oppositeSide[side], flipped, tooltipRect)) {
        side = oppositeSide[side];
        position = flipped;
      }
    }

    this.tooltipElement.classList.add(`app-tooltip-${side}`);
    this.applyPosition(this.clampToViewport(position, tooltipRect));
  }

  /** Only the axis the side moves along matters; the other one is clamped afterwards. */
  private fitsOnSide(side: TooltipPosition, position: Point, tooltipRect: DOMRect): boolean {
    const pad = this.viewportPaddingPx;
    return side === 'top' || side === 'bottom'
      ? position.top >= pad && position.top + tooltipRect.height <= window.innerHeight - pad
      : position.left >= pad && position.left + tooltipRect.width <= window.innerWidth - pad;
  }

  private computePositionFromRect(side: TooltipPosition, hostRect: DOMRect, tooltipRect: DOMRect, gap: number): Point {
    switch (side) {
      case 'top':
        return {
          left: hostRect.left + (hostRect.width - tooltipRect.width) / 2,
          top: hostRect.top - tooltipRect.height - gap
        };
      case 'bottom':
        return {
          left: hostRect.left + (hostRect.width - tooltipRect.width) / 2,
          top: hostRect.bottom + gap
        };
      case 'left':
        return {
          left: hostRect.left - tooltipRect.width - gap,
          top: hostRect.top + (hostRect.height - tooltipRect.height) / 2
        };
      case 'right':
        return {
          left: hostRect.right + gap,
          top: hostRect.top + (hostRect.height - tooltipRect.height) / 2
        };
    }
  }

  private computePositionFromPoint(side: TooltipPosition, clientX: number, clientY: number, tooltipRect: DOMRect, gap: number): Point {
    switch (side) {
      case 'top':
        return {
          left: clientX - tooltipRect.width / 2,
          top: clientY - tooltipRect.height - gap
        };
      case 'bottom':
        return {
          left: clientX - tooltipRect.width / 2,
          top: clientY + gap
        };
      case 'left':
        return {
          left: clientX - tooltipRect.width - gap,
          top: clientY - tooltipRect.height / 2
        };
      case 'right':
        return {
          left: clientX + gap,
          top: clientY - tooltipRect.height / 2
        };
    }
  }

  private clampToViewport(position: Point, tooltipRect: DOMRect): Point {
    const left = Math.max(
      this.viewportPaddingPx,
      Math.min(position.left, window.innerWidth - tooltipRect.width - this.viewportPaddingPx)
    );
    const top = Math.max(
      this.viewportPaddingPx,
      Math.min(position.top, window.innerHeight - tooltipRect.height - this.viewportPaddingPx)
    );

    return { left, top };
  }

  private applyPosition(position: Point): void {
    if (!this.tooltipElement) return;

    this.tooltipElement.style.left = `${position.left}px`;
    this.tooltipElement.style.top = `${position.top}px`;
  }
}
