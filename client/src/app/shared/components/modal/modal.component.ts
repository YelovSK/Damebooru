import { CommonModule } from '@angular/common';
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
  selector: 'app-modal',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './modal.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ModalComponent implements AfterViewInit {
  private readonly modalOverlayTemplate = viewChild<TemplateRef<unknown>>('modalOverlay');

  private readonly appOverlay = inject(AppOverlayService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly viewContainerRef = inject(ViewContainerRef);
  private overlayRef?: OverlayRef;
  private portal?: TemplatePortal;
  private viewReady = signal(false);

  open = input(false);
  title = input('');
  maxWidthClass = input('max-w-xl');
  closeOnBackdrop = input(true);
  showCloseButton = input(true);

  closed = output<void>();

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
    this.closed.emit();
  }

  private attachOverlay(): void {
    const template = this.modalOverlayTemplate();
    if (!template) {
      return;
    }

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

    const overlayRef = this.appOverlay.createCenteredModal();
    overlayRef
      .backdropClick()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        if (this.closeOnBackdrop()) {
          this.close();
        }
      });

    this.overlayRef = overlayRef;
    return overlayRef;
  }
}
