import { DOCUMENT } from '@angular/common';
import { Injectable, computed, effect, inject, signal } from '@angular/core';

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

export interface ThemeSettings {
  theme: AppTheme;
}

export type ImagePreloadMode = 'adaptive' | 'off' | 'conservative' | 'aggressive';
export type AppTheme =
  | 'midnight'
  | 'charcoal'
  | 'ember'
  | 'forest'
  | 'catppuccin-frappe'
  | 'catppuccin-macchiato'
  | 'catppuccin-mocha';

const DEFAULT_POST_SETTINGS: PostSettings = {
  autoPlayVideos: true,
  startVideosMuted: false,
  enablePostPreviewOnHover: true,
  postPreviewDelayMs: 700,
};

const DEFAULT_PERFORMANCE_SETTINGS: PerformanceSettings = {
  useScheduledImageSrc: true,
  scheduledImageAssignmentsPerFrame: 8,
  imagePreloadMode: 'adaptive',
};

const DEFAULT_THEME_SETTINGS: ThemeSettings = {
  theme: 'midnight',
};

const APP_THEMES = new Set<AppTheme>([
  'midnight',
  'charcoal',
  'ember',
  'forest',
  'catppuccin-frappe',
  'catppuccin-macchiato',
  'catppuccin-mocha',
]);

@Injectable({
  providedIn: 'root',
})
export class SettingsService {
  private readonly storage = inject(StorageService);
  private readonly document = inject(DOCUMENT);

  private readonly _postSettings = signal<PostSettings>(this.loadPostSettings());
  private readonly _performanceSettings = signal<PerformanceSettings>(
    this.loadPerformanceSettings(),
  );
  private readonly _themeSettings = signal<ThemeSettings>(this.loadThemeSettings());

  /** Reactive post settings */
  readonly postSettings = this._postSettings.asReadonly();
  readonly performanceSettings = this._performanceSettings.asReadonly();
  readonly themeSettings = this._themeSettings.asReadonly();

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

  readonly theme = computed(() => this._themeSettings().theme);

  constructor() {
    effect(() => {
      this.document.documentElement.dataset['theme'] = this.theme();
    });
  }

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

  private loadThemeSettings(): ThemeSettings {
    const saved = this.storage.getJson<Partial<ThemeSettings>>(
      STORAGE_KEYS.THEME_SETTINGS,
    );
    const theme = saved?.theme;

    return {
      ...DEFAULT_THEME_SETTINGS,
      ...saved,
      theme: theme && APP_THEMES.has(theme) ? theme : DEFAULT_THEME_SETTINGS.theme,
    };
  }

  updateThemeSettings(settings: Partial<ThemeSettings>): void {
    this._themeSettings.update(current => {
      const updated = {
        ...current,
        ...settings,
        theme: settings.theme && APP_THEMES.has(settings.theme)
          ? settings.theme
          : current.theme,
      };
      this.storage.setJson(STORAGE_KEYS.THEME_SETTINGS, updated);
      return updated;
    });
  }
}
