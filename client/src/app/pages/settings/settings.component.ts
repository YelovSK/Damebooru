import { Component, ChangeDetectionStrategy } from '@angular/core';
import { CommonModule } from '@angular/common';

import { TabsComponent } from '@shared/components/tabs/tabs.component';
import { TabComponent } from '@shared/components/tabs/tab.component';
import { PostSettingsComponent } from './post/post-settings.component';
import { AutoTaggingSettingsComponent } from './auto-tagging/auto-tagging-settings.component';
import { DuplicatesSettingsComponent } from './duplicates/duplicates-settings.component';
import { PerformanceSettingsComponent } from './performance/performance-settings.component';
import { ThemeSettingsComponent } from './theme/theme-settings.component';

@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [
    CommonModule,
    TabsComponent,
    TabComponent,
    PostSettingsComponent,
    AutoTaggingSettingsComponent,
    DuplicatesSettingsComponent,
    PerformanceSettingsComponent,
    ThemeSettingsComponent,
  ],
  templateUrl: './settings.component.html',
  styleUrl: './settings.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class SettingsComponent {}
