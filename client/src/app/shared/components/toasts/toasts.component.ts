import { Component, inject, ChangeDetectionStrategy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ToastService } from '@services/toast.service';
import { ProgressBarComponent } from '@shared/components/progress-bar/progress-bar.component';

@Component({
  selector: 'app-toasts',
  standalone: true,
  imports: [CommonModule, ProgressBarComponent],
  templateUrl: './toasts.component.html',
  styleUrl: './toasts.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ToastsComponent {
  toastService = inject(ToastService);
}
