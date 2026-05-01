import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';

import { SettingsService } from '@services/settings.service';
import { FormCheckboxComponent } from '@shared/components/form-checkbox/form-checkbox.component';
import { FormNumberInputComponent } from '@shared/components/form-number-input/form-number-input.component';

@Component({
  selector: 'app-performance-settings',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    FormCheckboxComponent,
    FormNumberInputComponent,
  ],
  templateUrl: './performance-settings.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PerformanceSettingsComponent {
  private readonly settingsService = inject(SettingsService);

  readonly settings = this.settingsService.performanceSettings;

  onScheduledImageSrcChange(value: boolean): void {
    this.settingsService.updatePerformanceSettings({
      useScheduledImageSrc: value,
    });
  }

  onScheduledImageAssignmentsPerFrameChange(value: number | null): void {
    const normalized =
      value === null || Number.isNaN(value)
        ? 10
        : Math.max(1, Math.min(16, Math.round(value)));

    this.settingsService.updatePerformanceSettings({
      scheduledImageAssignmentsPerFrame: normalized,
    });
  }
}
