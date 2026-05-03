import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  HostListener,
  TemplateRef,
  ViewChild,
  ViewContainerRef,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { OverlayRef } from '@angular/cdk/overlay';
import { TemplatePortal } from '@angular/cdk/portal';

import { ConfirmService } from '@services/confirm.service';
import { AppOverlayService } from '@services/app-overlay.service';
import { ButtonDirective } from '@shared/directives';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

@Component({
  selector: 'app-confirm-dialog',
  standalone: true,
  imports: [CommonModule, ButtonDirective],
  templateUrl: './confirm-dialog.component.html',
  styleUrl: './confirm-dialog.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ConfirmDialogComponent implements AfterViewInit {
  @ViewChild('confirmDialogOverlay')
  private confirmDialogOverlayTemplate?: TemplateRef<unknown>;

  confirmService = inject(ConfirmService);
  private readonly appOverlay = inject(AppOverlayService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly viewContainerRef = inject(ViewContainerRef);
  private overlayRef?: OverlayRef;
  private portal?: TemplatePortal;
  private viewReady = signal(false);

  protected typedConfirmation = signal('');
  protected readonly requiresTypedText = computed(() => this.confirmService.options()?.requireTypedText?.trim() ?? '');
  protected readonly canConfirm = computed(() => {
    const required = this.requiresTypedText();
    if (!required) {
      return true;
    }

    return this.typedConfirmation().trim() === required;
  });

  constructor() {
    effect(() => {
      this.confirmService.options();
      this.typedConfirmation.set('');
    });

    effect(() => {
      const options = this.confirmService.options();
      if (!this.viewReady()) {
        return;
      }

      if (options) {
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

  @HostListener('document:keydown.escape')
  onEscapeKey(): void {
    if (this.confirmService.options()) {
      this.cancel();
    }
  }

  confirm(): void {
    if (!this.canConfirm()) {
      return;
    }

    this.confirmService.resolve(true);
  }

  cancel(): void {
    this.confirmService.resolve(false);
  }

  onTypedConfirmationChange(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.typedConfirmation.set(value);
  }

  private attachOverlay(): void {
    if (!this.confirmDialogOverlayTemplate) {
      return;
    }

    const overlayRef = this.ensureOverlay();
    if (!this.portal) {
      this.portal = new TemplatePortal(
        this.confirmDialogOverlayTemplate,
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
      .subscribe(() => this.cancel());
    overlayRef
      .detachments()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        if (this.confirmService.options()) {
          this.cancel();
        }
      });

    this.overlayRef = overlayRef;
    return overlayRef;
  }
}
