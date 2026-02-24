import { ChangeDetectionStrategy, Component, computed, effect, HostListener, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';

import { ConfirmService } from '@services/confirm.service';
import { ButtonComponent } from '@shared/components/button/button.component';

@Component({
  selector: 'app-confirm-dialog',
  standalone: true,
  imports: [CommonModule, ButtonComponent],
  templateUrl: './confirm-dialog.component.html',
  styleUrl: './confirm-dialog.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ConfirmDialogComponent {
  confirmService = inject(ConfirmService);
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

  onBackdropClick(event: MouseEvent): void {
    if (event.target === event.currentTarget) {
      this.cancel();
    }
  }

  onTypedConfirmationChange(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.typedConfirmation.set(value);
  }
}
