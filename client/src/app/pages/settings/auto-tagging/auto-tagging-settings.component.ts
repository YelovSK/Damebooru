import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';

import { DamebooruService } from '@services/api/damebooru/damebooru.service';
import { AutoTagDiscoverySettings } from '@services/api/damebooru/models';
import { FormCheckboxComponent } from '@shared/components/form-checkbox/form-checkbox.component';

type AutoTagDiscoverySettingKey = keyof AutoTagDiscoverySettings;

@Component({
  selector: 'app-auto-tagging-settings',
  standalone: true,
  imports: [CommonModule, FormsModule, FormCheckboxComponent],
  templateUrl: './auto-tagging-settings.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AutoTaggingSettingsComponent {
  private readonly damebooru = inject(DamebooruService);

  readonly settings = signal<AutoTagDiscoverySettings | null>(null);
  readonly isLoading = signal(true);
  readonly isSaving = signal(false);
  readonly error = signal<string | null>(null);

  constructor() {
    this.loadSettings();
  }

  updateProvider(key: AutoTagDiscoverySettingKey, value: boolean): void {
    const current = this.settings();
    if (!current) {
      return;
    }

    const updated = { ...current, [key]: value };
    this.settings.set(updated);
    this.isSaving.set(true);
    this.error.set(null);

    this.damebooru.updateAutoTagDiscoverySettings(updated).subscribe({
      next: saved => {
        this.settings.set(saved);
        this.isSaving.set(false);
      },
      error: err => {
        this.settings.set(current);
        this.error.set(err.error?.description || 'Failed to update auto-tagging settings.');
        this.isSaving.set(false);
      },
    });
  }

  private loadSettings(): void {
    this.isLoading.set(true);
    this.error.set(null);

    this.damebooru.getAutoTagDiscoverySettings().subscribe({
      next: settings => {
        this.settings.set(settings);
        this.isLoading.set(false);
      },
      error: err => {
        this.error.set(err.error?.description || 'Failed to load auto-tagging settings.');
        this.isLoading.set(false);
      },
    });
  }
}
