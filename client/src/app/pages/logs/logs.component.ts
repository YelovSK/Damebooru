import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';

import { DamebooruService } from '../../services/api/damebooru/damebooru.service';
import { AppLogEntry } from '../../services/api/damebooru/models';
import { DateTimePipe } from '../../shared/pipes/date-time.pipe';
import { ButtonComponent } from '../../shared/components/button/button.component';
import { FormDropdownComponent, FormDropdownOption } from '../../shared/components/dropdown/form-dropdown.component';
import { SearchInputComponent } from '../../shared/components/search-input/search-input.component';
import { ToastService } from '../../services/toast.service';

type LogLevelFilter = 'warning' | 'error' | 'critical' | 'information' | 'debug' | 'trace';

@Component({
  selector: 'app-logs-page',
  standalone: true,
  imports: [CommonModule, DateTimePipe, ButtonComponent, FormDropdownComponent, SearchInputComponent],
  templateUrl: './logs.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LogsPageComponent {
  private readonly api = inject(DamebooruService);
  private readonly toast = inject(ToastService);

  readonly logs = signal<AppLogEntry[]>([]);
  readonly hasMore = signal(false);

  readonly level = signal<LogLevelFilter>('warning');
  readonly categoryQuery = signal('');
  readonly textQuery = signal('');

  readonly levelOptions: FormDropdownOption<LogLevelFilter>[] = [
    { label: 'Warning', value: 'warning' },
    { label: 'Error', value: 'error' },
    { label: 'Critical', value: 'critical' },
    { label: 'Information', value: 'information' },
    { label: 'Debug', value: 'debug' },
    { label: 'Trace', value: 'trace' },
  ];

  constructor() {
    this.loadLogs(true);
  }

  onLevelChange(level: LogLevelFilter | null): void {
    this.level.set(level ?? 'warning');
    this.loadLogs(true);
  }

  onCategorySearch(value: string): void {
    this.categoryQuery.set(value);
    this.loadLogs(true);
  }

  onTextSearch(value: string): void {
    this.textQuery.set(value);
    this.loadLogs(true);
  }

  loadMore(): void {
    if (!this.hasMore()) {
      return;
    }

    this.loadLogs(false);
  }

  getLevelClass(level: string): string {
    switch (level.toLowerCase()) {
      case 'critical':
        return 'border-red-300/60 bg-red-500/20 text-red-200';
      case 'error':
        return 'border-rose-300/60 bg-rose-500/20 text-rose-200';
      case 'warning':
        return 'border-amber-300/60 bg-amber-500/20 text-amber-200';
      case 'information':
        return 'border-sky-300/60 bg-sky-500/20 text-sky-200';
      case 'debug':
        return 'border-emerald-300/60 bg-emerald-500/20 text-emerald-200';
      case 'trace':
        return 'border-slate-300/50 bg-slate-500/20 text-slate-200';
      default:
        return 'border-gray-300/40 bg-gray-500/20 text-gray-200';
    }
  }

  private loadLogs(reset: boolean): void {
    const current = this.logs();
    const beforeId = !reset && current.length > 0
      ? current[current.length - 1].id
      : undefined;

    this.api.getLogs({
      level: this.level(),
      category: this.categoryQuery().trim() || undefined,
      contains: this.textQuery().trim() || undefined,
      beforeId,
      take: 20,
    }).subscribe({
      next: response => {
        this.hasMore.set(response.hasMore);
        this.logs.set(reset ? response.items : [...current, ...response.items]);
      },
      error: () => {
        this.toast.error('Failed to load logs');
      },
    });
  }
}
