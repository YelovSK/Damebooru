import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  TemplateRef,
  ViewChild,
  ViewContainerRef,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { OverlayRef } from '@angular/cdk/overlay';
import { TemplatePortal } from '@angular/cdk/portal';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { AppOverlayService } from '@services/app-overlay.service';

@Component({
  selector: 'app-mobile-bottom-sheet',
  standalone: true,
  templateUrl: './mobile-bottom-sheet.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MobileBottomSheetComponent implements AfterViewInit {
  @ViewChild('bottomSheetOverlay')
  private bottomSheetOverlayTemplate?: TemplateRef<unknown>;

  open = input(false);
  openChange = output<boolean>();

  private readonly appOverlay = inject(AppOverlayService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly viewContainerRef = inject(ViewContainerRef);
  private overlayRef?: OverlayRef;
  private portal?: TemplatePortal;
  private viewReady = signal(false);

  constructor() {
    effect(() => {
      if (!this.viewReady()) {
        return;
      }

      if (this.open()) {
        this.attachOverlay();
      } else {
        this.overlayRef?.detach();
      }
    });

    this.destroyRef.onDestroy(() => this.overlayRef?.dispose());
  }

  ngAfterViewInit(): void {
    this.viewReady.set(true);
  }

  close(): void {
    this.openChange.emit(false);
  }

  private attachOverlay(): void {
    if (!this.bottomSheetOverlayTemplate) {
      return;
    }

    const overlayRef = this.ensureOverlay();
    if (!this.portal) {
      this.portal = new TemplatePortal(
        this.bottomSheetOverlayTemplate,
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
}
