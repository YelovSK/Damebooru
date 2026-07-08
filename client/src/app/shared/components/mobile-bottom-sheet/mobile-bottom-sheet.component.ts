import {
  type AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  type TemplateRef,
  ViewContainerRef,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { type OverlayRef } from '@angular/cdk/overlay';
import { TemplatePortal } from '@angular/cdk/portal';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { AppOverlayService } from '@services/app-overlay.service';

@Component({
  selector: 'app-mobile-bottom-sheet',
  standalone: true,
  templateUrl: './mobile-bottom-sheet.component.html',
  styleUrl: './mobile-bottom-sheet.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MobileBottomSheetComponent implements AfterViewInit {
  private readonly bottomSheetOverlayTemplate = viewChild<TemplateRef<unknown>>('bottomSheetOverlay');

  open = input(false);
  openChange = output<boolean>();
  closing = signal(false);

  private readonly appOverlay = inject(AppOverlayService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly viewContainerRef = inject(ViewContainerRef);
  private overlayRef?: OverlayRef;
  private portal?: TemplatePortal;
  private viewReady = signal(false);
  private closeTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    effect(() => {
      if (!this.viewReady()) {
        return;
      }

      if (this.open()) {
        this.attachOverlay();
      } else {
        this.detachOverlay();
      }
    });

    this.destroyRef.onDestroy(() => {
      this.clearCloseTimer();
      this.overlayRef?.dispose();
    });
  }

  ngAfterViewInit(): void {
    this.viewReady.set(true);
  }

  close(): void {
    this.openChange.emit(false);
  }

  private attachOverlay(): void {
    const template = this.bottomSheetOverlayTemplate();
    if (!template) {
      return;
    }

    this.clearCloseTimer();
    this.closing.set(false);

    const overlayRef = this.ensureOverlay();
    if (!this.portal) {
      this.portal = new TemplatePortal(
        template,
        this.viewContainerRef,
      );
    }

    if (!overlayRef.hasAttached()) {
      overlayRef.attach(this.portal);
    }
  }

  private ensureOverlay(): OverlayRef {
    if (this.overlayRef) {
      return this.overlayRef;
    }

    const overlayRef = this.appOverlay.createBottomSheet();
    overlayRef
      .backdropClick()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.close());
    overlayRef
      .detachments()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.openChange.emit(false));

    this.overlayRef = overlayRef;
    return overlayRef;
  }

  private detachOverlay(): void {
    if (!this.overlayRef?.hasAttached()) {
      return;
    }

    if (this.closing()) {
      return;
    }

    this.closing.set(true);
    this.closeTimer = setTimeout(() => {
      this.closeTimer = null;
      this.closing.set(false);
      this.overlayRef?.detach();
    }, 180);
  }

  private clearCloseTimer(): void {
    if (this.closeTimer === null) {
      return;
    }

    clearTimeout(this.closeTimer);
    this.closeTimer = null;
  }
}
