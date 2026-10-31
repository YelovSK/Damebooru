import { ChangeDetectionStrategy, Component, DestroyRef, type ElementRef, computed, effect, inject, input, linkedSignal, signal, viewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, interval, of } from 'rxjs';
import { FormsModule } from '@angular/forms';
import cronstrue from 'cronstrue';

import { DamebooruService } from '../../services/api/damebooru/damebooru.service';
import { type CronPreview, type JobExecution, type JobKey, type JobMode, type JobState, JobStatus, type JobViewModel, type ScheduledJob } from '../../services/api/damebooru/models';
import { ButtonDirective, InputDirective } from '@shared/directives';
import { FormCheckboxComponent } from '../../shared/components/form-checkbox/form-checkbox.component';
import { CollapsibleComponent } from '../../shared/components/collapsible/collapsible.component';
import { PaginatorComponent } from '../../shared/components/paginator/paginator.component';
import { ProgressBarComponent } from '../../shared/components/progress-bar/progress-bar.component';
import { type DataTableColumn, DataTableComponent } from '../../shared/components/data-table/data-table.component';
import { TooltipDirective } from '../../shared/directives';
import { formatDateTime } from '../../shared/pipes/date-time.pipe';
import { RelativeDurationPipe } from '../../shared/pipes/relative-duration.pipe';
import { ToastService } from '../../services/toast.service';
import { ConfirmService } from '../../services/confirm.service';
import { PageComponent } from '@shared/components/page/page.component';
import { JOB_HISTORY_PAGE_SIZE, type JobsPageData } from './jobs.resolver';

const LIVE_JOBS_POLL_MS = 2000;
const CLOCK_TICK_MS = 1000;
const CRON_PREVIEW_DEBOUNCE_MS = 350;
const UPCOMING_RUN_COUNT = 3;

const JOB_STATUS_LABELS: Record<JobStatus, string> = {
  [JobStatus.Idle]: 'Idle',
  [JobStatus.Running]: 'Running',
  [JobStatus.Completed]: 'Completed',
  [JobStatus.Failed]: 'Failed',
  [JobStatus.Cancelled]: 'Cancelled'
};

const JOB_STATUS_CLASSES: Partial<Record<JobStatus, string>> = {
  [JobStatus.Running]: 'text-accent',
  [JobStatus.Failed]: 'text-danger',
  [JobStatus.Cancelled]: 'text-warning',
};

function describeCron(expression: string): string {
  try {
    return cronstrue.toString(expression, { use24HourTimeFormat: true });
  } catch {
    return expression;
  }
}

interface CronDraft {
  scheduleId: number;
  text: string;
  preview?: CronPreview;
}

interface JobRow {
  job: JobViewModel;
  schedule?: ScheduledJob;
}

@Component({
  selector: 'app-jobs-page',
  standalone: true,
  imports: [PageComponent, CommonModule, FormsModule, ButtonDirective, InputDirective, FormCheckboxComponent, CollapsibleComponent, PaginatorComponent, ProgressBarComponent, DataTableComponent, TooltipDirective, RelativeDurationPipe],
  templateUrl: './jobs.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class JobsPageComponent {
  private readonly damebooru = inject(DamebooruService);
  private readonly toast = inject(ToastService);
  private readonly confirmService = inject(ConfirmService);
  private readonly destroyRef = inject(DestroyRef);

  readonly data = input.required<JobsPageData>();

  readonly jobs = linkedSignal(() => this.data().jobs);
  readonly schedules = linkedSignal(() => this.data().schedules);
  readonly history = linkedSignal(() => this.data().history.items);
  readonly historyTotal = linkedSignal(() => this.data().history.total);
  readonly historyPage = signal(1);
  readonly historyTotalPages = computed(() => Math.max(1, Math.ceil(this.historyTotal() / JOB_HISTORY_PAGE_SIZE)));

  readonly rows = computed<JobRow[]>(() => {
    const schedulesByKey = new Map(this.schedules().map(schedule => [schedule.jobKey, schedule]));
    return this.jobs().map(job => ({ job, schedule: schedulesByKey.get(job.key) }));
  });

  readonly scheduleDescriptions = computed(() => new Map(this.schedules().map(schedule => [schedule.id, describeCron(schedule.cronExpression)])));

  /** The schedule whose cron expression is open for editing; only one at a time. */
  readonly cronDraft = signal<CronDraft | null>(null);
  private readonly cronEditor = viewChild<ElementRef<HTMLInputElement>>('cronEditor');
  readonly now = signal(Date.now());

  readonly historyColumns: DataTableColumn<JobExecution>[] = [
    {
      key: 'jobName',
      label: 'Job',
      value: run => run.jobName,
    },
    {
      key: 'status',
      label: 'Status',
      value: run => this.getStatusText(run.status),
      cellClass: run => JOB_STATUS_CLASSES[run.status] ?? 'text-fg-muted',
    },
    {
      key: 'startTime',
      label: 'Started',
      value: run => formatDateTime(run.startTime),
      cellClass: () => 'text-fg-muted whitespace-nowrap',
    },
    {
      key: 'duration',
      label: 'Duration',
      value: run => this.getHistoryDuration(run),
      cellClass: () => 'text-fg-muted',
    },
    {
      key: 'result',
      label: 'Result',
      value: run => run.status === JobStatus.Failed && run.errorMessage?.trim() ? run.errorMessage.trim() : this.formatHistoryState(run),
      cellClass: run => run.status === JobStatus.Failed ? 'text-danger' : 'text-fg',
    },
  ];

  readonly cronExamples = [
    'Every 6 hours: 0 */6 * * *',
    'Every day at 03:00: 0 3 * * *',
    'Every Sunday at 03:00: 0 3 * * 0',
    'Every 15 minutes: */15 * * * *'
  ];

  private cronPreviewTimer?: ReturnType<typeof setTimeout>;

  constructor() {
    this.destroyRef.onDestroy(() => clearTimeout(this.cronPreviewTimer));
    effect(() => this.cronEditor()?.nativeElement.focus());

    interval(LIVE_JOBS_POLL_MS)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.refreshLiveJobs());

    interval(CLOCK_TICK_MS)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.now.set(Date.now()));
  }

  loadHistory() {
    this.damebooru.getJobHistory(JOB_HISTORY_PAGE_SIZE, this.historyPage()).subscribe(data => {
      this.history.set(data.items);
      this.historyTotal.set(data.total);
    });
  }

  onHistoryPageChange(page: number) {
    this.historyPage.set(page);
    this.loadHistory();
  }

  runJob(key: JobKey, mode: JobMode) {
    this.damebooru.startJob(key, mode).subscribe({
      next: () => this.refreshLiveJobs(),
      error: (err) => this.toast.error('Failed to start job: ' + err.message)
    });
  }

  cancelJob(id: string) {
    this.confirmService.confirm({
      title: 'Cancel Job',
      message: 'Are you sure you want to cancel this job?',
      confirmText: 'Cancel Job',
      variant: 'danger'
    }).subscribe(confirmed => {
      if (!confirmed) return;
      this.damebooru.cancelJob(id).subscribe(() => this.refreshLiveJobs());
    });
  }

  editCron(schedule: ScheduledJob): void {
    clearTimeout(this.cronPreviewTimer);
    this.cronDraft.set({ scheduleId: schedule.id, text: schedule.cronExpression });
  }

  onCronInput(value: string): void {
    this.cronDraft.update(draft => draft && { ...draft, text: value });
    this.queueCronPreview(value);
  }

  commitCron(schedule: ScheduledJob): void {
    const draft = this.cronDraft();
    if (draft?.scheduleId !== schedule.id) {
      return;
    }

    const text = draft.text.trim();
    if (text === schedule.cronExpression) {
      this.closeCronEditor();
      return;
    }

    this.saveSchedule(schedule, { cronExpression: text, isEnabled: schedule.isEnabled });
  }

  closeCronEditor(): void {
    clearTimeout(this.cronPreviewTimer);
    this.cronDraft.set(null);
  }

  setEnabled(schedule: ScheduledJob, isEnabled: boolean): void {
    this.saveSchedule(schedule, { cronExpression: schedule.cronExpression, isEnabled });
  }

  formatRuns(runs: string[]): string {
    return runs.map(run => formatDateTime(run)).join('  ·  ');
  }

  getStatusText(status: JobStatus): string {
    return JOB_STATUS_LABELS[status] ?? 'Unknown';
  }

  getProgressPercent(state?: JobState): number {
    if (!state || typeof state.progressCurrent !== 'number' || typeof state.progressTotal !== 'number' || state.progressTotal <= 0) {
      return 0;
    }

    return Math.max(0, Math.min(100, (state.progressCurrent / state.progressTotal) * 100));
  }

  isDeterminateProgress(state?: JobState): boolean {
    return !!state
      && typeof state.progressCurrent === 'number'
      && typeof state.progressTotal === 'number'
      && state.progressTotal > 0;
  }

  formatHistoryState(run: JobExecution): string {
    const state = run.state;
    if (!state) return '-';

    // While running, prioritize live phase text so it's obvious what the job is doing.
    if (run.status === JobStatus.Running) {
      const activityText = state.activityText?.trim();
      if (activityText) return activityText;
    }

    const finalText = state.finalText?.trim();
    if (finalText) return finalText;

    const activityText = state.activityText?.trim();
    if (activityText) return activityText;

    if (typeof state.progressCurrent === 'number' && typeof state.progressTotal === 'number') {
      return `processed ${state.progressCurrent} of ${state.progressTotal}`;
    }

    return '-';
  }

  trackHistoryRow = (row: JobExecution): number => row.id;

  private saveSchedule(schedule: ScheduledJob, update: Pick<ScheduledJob, 'cronExpression' | 'isEnabled'>): void {
    this.damebooru.updateJobSchedule(schedule.id, update).subscribe({
      next: saved => {
        this.schedules.update(schedules => schedules.map(s => s.id === saved.id ? saved : s));
        if (this.cronDraft()?.scheduleId === saved.id) {
          this.closeCronEditor();
        }
      },
      error: (err) => this.toast.error('Failed to update schedule: ' + err.message)
    });
  }

  private getHistoryDuration(run: JobExecution): string {
    const startMs = Date.parse(run.startTime);
    const endMs = run.endTime ? Date.parse(run.endTime) : run.status === JobStatus.Running ? this.now() : NaN;

    if (!Number.isFinite(startMs) || !Number.isFinite(endMs)) {
      return '-';
    }

    const diffMs = Math.max(0, endMs - startMs);
    if (diffMs < 1000) {
      return `${Math.round(diffMs)} ms`;
    }

    if (diffMs < 60_000) {
      return `${(diffMs / 1000).toFixed(1)}s`;
    }

    const totalSeconds = Math.floor(diffMs / 1000);
    return `${Math.floor(totalSeconds / 60)}m ${totalSeconds % 60}s`;
  }

  private refreshLiveJobs(): void {
    this.damebooru.getJobs().pipe(
      takeUntilDestroyed(this.destroyRef),
      catchError(() => of([]))
    ).subscribe(data => {
      const previousRunningIds = this.getRunningExecutionIds(this.jobs());
      this.jobs.set(data);
      this.patchHistoryWithRunningJobs(data);

      const currentRunningIds = this.getRunningExecutionIds(data);
      if ([...previousRunningIds].some(id => !currentRunningIds.has(id))) {
        this.loadHistory();
      }
    });
  }

  private patchHistoryWithRunningJobs(jobs: JobViewModel[]): void {
    const running = jobs
      .map(j => j.activeJobInfo)
      .filter((info): info is NonNullable<JobViewModel['activeJobInfo']> =>
        !!info && info.status === JobStatus.Running && typeof info.executionId === 'number' && !!info.state);

    if (running.length === 0) {
      return;
    }

    const runningByExecutionId = new Map<number, typeof running[number]>();
    for (const run of running) {
      runningByExecutionId.set(run.executionId!, run);
    }

    this.history.update(items => {
      const patched: JobExecution[] = items.map(item => {
        const live = runningByExecutionId.get(item.id);
        if (!live) {
          return item;
        }

        return {
          ...item,
          status: JobStatus.Running,
          endTime: undefined,
          state: live.state
        };
      });

      if (this.historyPage() !== 1) {
        return patched;
      }

      const existingIds = new Set(patched.map(item => item.id));
      const missingRunningRows: JobExecution[] = [];
      for (const live of running) {
        const executionId = live.executionId!;
        if (existingIds.has(executionId)) {
          continue;
        }

        missingRunningRows.push({
          id: executionId,
          jobKey: live.key,
          jobName: live.name,
          status: JobStatus.Running,
          startTime: live.startTime ?? new Date().toISOString(),
          endTime: undefined,
          errorMessage: undefined,
          state: live.state
        });
      }

      if (missingRunningRows.length === 0) {
        return patched;
      }

      missingRunningRows.sort((a, b) => {
        return new Date(b.startTime).getTime() - new Date(a.startTime).getTime();
      });

      return [...missingRunningRows, ...patched].slice(0, JOB_HISTORY_PAGE_SIZE);
    });
  }

  private getRunningExecutionIds(jobs: JobViewModel[]): Set<number> {
    const ids = new Set<number>();
    for (const info of jobs.map(j => j.activeJobInfo)) {
      if (info?.status === JobStatus.Running && typeof info.executionId === 'number') {
        ids.add(info.executionId);
      }
    }
    return ids;
  }

  private queueCronPreview(expression: string): void {
    const normalized = expression.trim();
    clearTimeout(this.cronPreviewTimer);

    if (!normalized) {
      this.setCronPreview({ isValid: false, error: 'Cron expression is required.', nextRuns: [] });
      return;
    }

    this.cronPreviewTimer = setTimeout(() => {
      this.damebooru.previewCronExpression(normalized, UPCOMING_RUN_COUNT).subscribe({
        next: preview => {
          if (this.cronDraft()?.text.trim() === normalized) {
            this.setCronPreview(preview);
          }
        },
        error: () => {
          if (this.cronDraft()?.text.trim() === normalized) {
            this.setCronPreview({ isValid: false, error: 'Failed to validate cron expression.', nextRuns: [] });
          }
        }
      });
    }, CRON_PREVIEW_DEBOUNCE_MS);
  }

  private setCronPreview(preview: CronPreview): void {
    this.cronDraft.update(draft => draft && { ...draft, preview });
  }
}
