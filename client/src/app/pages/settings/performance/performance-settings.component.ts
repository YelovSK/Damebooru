import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';

import { SettingsService, type ImagePreloadMode } from '@services/settings.service';
import { FormCheckboxComponent } from '@shared/components/form-checkbox/form-checkbox.component';
import { FormNumberInputComponent } from '@shared/components/form-number-input/form-number-input.component';
import { FormDropdownComponent, type FormDropdownOption } from '@shared/components/dropdown/form-dropdown.component';

@Component({
  selector: 'app-performance-settings',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    FormCheckboxComponent,
    FormNumberInputComponent,
    FormDropdownComponent,
  ],
  templateUrl: './performance-settings.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PerformanceSettingsComponent {
  private readonly settingsService = inject(SettingsService);

  readonly settings = this.settingsService.performanceSettings;
  readonly imagePreloadModeOptions: FormDropdownOption<ImagePreloadMode>[] = [
    { label: 'Adaptive', value: 'adaptive' },
    { label: 'Off', value: 'off' },
    { label: 'Conservative', value: 'conservative' },
    { label: 'Aggressive', value: 'aggressive' },
  ];

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

  onImagePreloadModeChange(value: ImagePreloadMode | null): void {
    this.settingsService.updatePerformanceSettings({
      imagePreloadMode: value ?? 'adaptive',
    });
  }
}
