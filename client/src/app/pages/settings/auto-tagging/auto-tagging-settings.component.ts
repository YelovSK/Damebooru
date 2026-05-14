import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';

import { DamebooruService } from '@services/api/damebooru/damebooru.service';
import { type AiTaggingSettings, type AutoTagDiscoverySettings } from '@services/api/damebooru/models';
import { FormCheckboxComponent } from '@shared/components/form-checkbox/form-checkbox.component';
import { FormNumberInputComponent } from '@shared/components/form-number-input/form-number-input.component';
import { ButtonDirective } from '@shared/directives';

type AutoTagDiscoverySettingKey = keyof AutoTagDiscoverySettings;

@Component({
  selector: 'app-auto-tagging-settings',
  standalone: true,
  imports: [CommonModule, FormsModule, FormCheckboxComponent, FormNumberInputComponent, ButtonDirective],
  templateUrl: './auto-tagging-settings.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AutoTaggingSettingsComponent {
  private readonly damebooru = inject(DamebooruService);

  readonly settings = signal<AutoTagDiscoverySettings | null>(null);
  readonly aiSettings = signal<AiTaggingSettings | null>(null);
  readonly aiDraft = signal<AiTaggingSettings | null>(null);
  readonly isLoading = signal(true);
  readonly isAiLoading = signal(true);
  readonly isSaving = signal(false);
  readonly isAiSaving = signal(false);
  readonly error = signal<string | null>(null);
  readonly aiError = signal<string | null>(null);

  readonly hasAiChanges = computed(() => {
    const settings = this.aiSettings();
    const draft = this.aiDraft();
    return !!settings
      && !!draft
      && (settings.suggestionThreshold !== draft.suggestionThreshold
        || settings.applyThreshold !== draft.applyThreshold);
  });

  constructor() {
    this.loadSettings();
    this.loadAiSettings();
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

  updateAiSuggestionThreshold(value: number | null): void {
    this.updateAiDraft({
      suggestionThreshold: this.normalizeThreshold(value, 0.492),
    });
  }

  updateAiApplyThreshold(value: number | null): void {
    this.updateAiDraft({
      applyThreshold: this.normalizeThreshold(value, 0.7),
    });
  }

  saveAiSettings(): void {
    const draft = this.aiDraft();
    if (!draft || this.isAiSaving()) {
      return;
    }

    this.isAiSaving.set(true);
    this.aiError.set(null);

    this.damebooru.updateAiTaggingSettings(draft).subscribe({
      next: saved => {
        this.aiSettings.set(saved);
        this.aiDraft.set(saved);
        this.isAiSaving.set(false);
      },
      error: err => {
        this.aiError.set(this.resolveError(err, 'Failed to update AI tagging settings.'));
        this.isAiSaving.set(false);
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

  private loadAiSettings(): void {
    this.isAiLoading.set(true);
    this.aiError.set(null);

    this.damebooru.getAiTaggingSettings().subscribe({
      next: settings => {
        this.aiSettings.set(settings);
        this.aiDraft.set(settings);
        this.isAiLoading.set(false);
      },
      error: err => {
        this.aiError.set(this.resolveError(err, 'Failed to load AI tagging settings.'));
        this.isAiLoading.set(false);
      },
    });
  }

  private updateAiDraft(settings: Partial<AiTaggingSettings>): void {
    const current = this.aiDraft();
    if (!current) {
      return;
    }

    this.aiDraft.set({ ...current, ...settings });
    this.aiError.set(null);
  }

  private normalizeThreshold(value: number | null, fallback: number): number {
    if (value === null || Number.isNaN(value)) {
      return fallback;
    }

    return Math.max(0.01, Math.min(1, Number(value.toFixed(3))));
  }

  private resolveError(err: unknown, fallback: string): string {
    if (typeof err === 'object' && err !== null && 'error' in err) {
      const error = (err as { error?: unknown }).error;
      if (typeof error === 'string' && error.trim().length > 0) {
        return error;
      }

      if (typeof error === 'object' && error !== null && 'description' in error) {
        const description = (error as { description?: unknown }).description;
        if (typeof description === 'string' && description.trim().length > 0) {
          return description;
        }
      }
    }

    return fallback;
  }
}
