import { DOCUMENT, CommonModule } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  effect,
  inject,
  input,
  output,
} from '@angular/core';

@Component({
  selector: 'app-modal',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './modal.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ModalComponent {
  private static bodyScrollLocks = 0;
  private static previousBodyOverflow = '';

  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);
  private lockedByThisModal = false;

  open = input(false);
  title = input('');
  maxWidthClass = input('max-w-xl');
  closeOnBackdrop = input(true);
  showCloseButton = input(true);

  closed = output<void>();

  constructor() {
    effect(() => {
      this.syncBodyScrollLock(this.open());
    });

    this.destroyRef.onDestroy(() => {
      this.syncBodyScrollLock(false);
    });
  }

  close(): void {
    this.closed.emit();
  }

  onBackdropClick(): void {
    if (!this.closeOnBackdrop()) return;
    this.close();
  }

  private syncBodyScrollLock(shouldLock: boolean): void {
    const body = this.document?.body;
    if (!body) {
      return;
    }

    if (shouldLock) {
      if (this.lockedByThisModal) {
        return;
      }

      if (ModalComponent.bodyScrollLocks === 0) {
        ModalComponent.previousBodyOverflow = body.style.overflow;
        body.style.overflow = 'hidden';
      }

      ModalComponent.bodyScrollLocks += 1;
      this.lockedByThisModal = true;
      return;
    }

    if (!this.lockedByThisModal) {
      return;
    }

    ModalComponent.bodyScrollLocks = Math.max(0, ModalComponent.bodyScrollLocks - 1);
    this.lockedByThisModal = false;

    if (ModalComponent.bodyScrollLocks === 0) {
      body.style.overflow = ModalComponent.previousBodyOverflow;
    }
  }
}
