import { Injectable, inject, signal, computed } from '@angular/core';

import { StorageService, STORAGE_KEYS } from './storage.service';

export interface PostSettings {
  autoPlayVideos: boolean;
  startVideosMuted: boolean;
  enablePostPreviewOnHover: boolean;
  postPreviewDelayMs: number;
}

export interface PerformanceSettings {
  useScheduledImageSrc: boolean;
  scheduledImageAssignmentsPerFrame: number;
  imagePreloadMode: ImagePreloadMode;
}

export type ImagePreloadMode = 'adaptive' | 'off' | 'conservative' | 'aggressive';

const DEFAULT_POST_SETTINGS: PostSettings = {
  autoPlayVideos: true,
  startVideosMuted: false,
  enablePostPreviewOnHover: true,
  postPreviewDelayMs: 700,
};

const DEFAULT_PERFORMANCE_SETTINGS: PerformanceSettings = {
  useScheduledImageSrc: true,
  scheduledImageAssignmentsPerFrame: 10,
  imagePreloadMode: 'adaptive',
};

@Injectable({
  providedIn: 'root',
})
export class SettingsService {
  private readonly storage = inject(StorageService);

  private readonly _postSettings = signal<PostSettings>(this.loadPostSettings());
  private readonly _performanceSettings = signal<PerformanceSettings>(
    this.loadPerformanceSettings(),
  );

  /** Reactive post settings */
  readonly postSettings = this._postSettings.asReadonly();
  readonly performanceSettings = this._performanceSettings.asReadonly();

  /** Convenience computed for auto-play videos */
  readonly autoPlayVideos = computed(() => this._postSettings().autoPlayVideos);

  /** Convenience computed for start muted */
  readonly startVideosMuted = computed(() => this._postSettings().startVideosMuted);

  /** Convenience computed for hover preview enabled */
  readonly enablePostPreviewOnHover = computed(() => this._postSettings().enablePostPreviewOnHover);

  /** Convenience computed for hover preview delay */
  readonly postPreviewDelayMs = computed(() => this._postSettings().postPreviewDelayMs);

  readonly useScheduledImageSrc = computed(
    () => this._performanceSettings().useScheduledImageSrc,
  );

  private loadPostSettings(): PostSettings {
    const saved = this.storage.getJson<PostSettings>(STORAGE_KEYS.POST_SETTINGS);
    return { ...DEFAULT_POST_SETTINGS, ...saved };
  }

  updatePostSettings(settings: Partial<PostSettings>): void {
    this._postSettings.update(current => {
      const updated = { ...current, ...settings };
      this.storage.setJson(STORAGE_KEYS.POST_SETTINGS, updated);
      return updated;
    });
  }

  private loadPerformanceSettings(): PerformanceSettings {
    const saved = this.storage.getJson<PerformanceSettings>(
      STORAGE_KEYS.PERFORMANCE_SETTINGS,
    );
    return { ...DEFAULT_PERFORMANCE_SETTINGS, ...saved };
  }

  updatePerformanceSettings(settings: Partial<PerformanceSettings>): void {
    this._performanceSettings.update(current => {
      const updated = { ...current, ...settings };
      this.storage.setJson(STORAGE_KEYS.PERFORMANCE_SETTINGS, updated);
      return updated;
    });
  }
}
