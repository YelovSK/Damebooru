import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';

import { type AppTheme, SettingsService } from '@services/settings.service';

interface ThemeOption {
  value: AppTheme;
  label: string;
}

@Component({
  selector: 'app-theme-settings',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './theme-settings.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ThemeSettingsComponent {
  private readonly settingsService = inject(SettingsService);

  readonly settings = this.settingsService.themeSettings;
  readonly themeOptions: readonly ThemeOption[] = [
    {
      value: 'midnight',
      label: 'Midnight',
    },
    {
      value: 'charcoal',
      label: 'Charcoal',
    },
    {
      value: 'ember',
      label: 'Ember',
    },
    {
      value: 'forest',
      label: 'Forest',
    },
    {
      value: 'catppuccin-frappe',
      label: 'Catppuccin Frappe',
    },
    {
      value: 'catppuccin-macchiato',
      label: 'Catppuccin Macchiato',
    },
    {
      value: 'catppuccin-mocha',
      label: 'Catppuccin Mocha',
    },
  ];

  selectTheme(theme: AppTheme): void {
    this.settingsService.updateThemeSettings({ theme });
  }
}
