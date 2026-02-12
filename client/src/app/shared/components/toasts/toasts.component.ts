import { Component, inject, ChangeDetectionStrategy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { animate, style, transition, trigger } from '@angular/animations';
import { ToastService } from '@services/toast.service';
import { ProgressBarComponent } from '@shared/components/progress-bar/progress-bar.component';

@Component({
  selector: 'app-toasts',
  standalone: true,
  imports: [CommonModule, ProgressBarComponent],
  templateUrl: './toasts.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  animations: [
    trigger('toastAnimation', [
      transition(':enter', [
        style({ transform: 'translateX(100%)', opacity: 0 }),
        animate('300ms ease-out', style({ transform: 'translateX(0)', opacity: 1 }))
      ]),
      transition(':leave', [
        animate('200ms ease-in', style({ transform: 'translateX(100%)', opacity: 0 }))
      ])
    ])
  ]
})
export class ToastsComponent {
  toastService = inject(ToastService);
}
