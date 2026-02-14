import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, interval } from 'rxjs';
import { FormsModule } from '@angular/forms';
import dayjs from 'dayjs/esm';

import { BakabooruService } from '../../services/api/bakabooru/bakabooru.service';
import { JobExecution, JobMode, JobState, JobStatus, JobViewModel, ScheduledJob } from '../../services/api/bakabooru/models';
import { ButtonComponent } from '../../shared/components/button/button.component';
import { PaginatorComponent } from '../../shared/components/paginator/paginator.component';
import { ProgressBarComponent } from '../../shared/components/progress-bar/progress-bar.component';
import { ToastService } from '../../services/toast.service';
import { ConfirmService } from '../../services/confirm.service';

const LIVE_JOBS_POLL_MS = 2000;
const CLOCK_TICK_MS = 1000;
const HISTORY_PAGE_SIZE = 20;
const DATE_TIME_FORMAT = 'YYYY-MM-DD HH:mm:ss';

const JOB_STATUS_LABELS: Record<JobStatus, string> = {
  [JobStatus.Idle]: 'Idle',
  [JobStatus.Running]: 'Running',
  [JobStatus.Completed]: 'Completed',
  [JobStatus.Failed]: 'Failed',
  [JobStatus.Cancelled]: 'Cancelled'
};

@Component({
  selector: 'app-jobs-page',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonComponent, PaginatorComponent, ProgressBarComponent],
  templateUrl: './jobs.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class JobsPageComponent {
  private readonly bakabooru = inject(BakabooruService);
  private readonly toast = inject(ToastService);
  private readonly confirmService = inject(ConfirmService);
  private readonly destroyRef = inject(DestroyRef);

  readonly jobStatus = JobStatus;
  readonly jobs = signal<JobViewModel[]>([]);
  readonly schedules = signal<ScheduledJob[]>([]);
  readonly history = signal<JobExecution[]>([]);
  readonly now = signal(Date.now());
  readonly historyPage = signal(1);
  readonly historyTotal = signal(0);
  readonly historyTotalPages = computed(() => Math.max(1, Math.ceil(this.historyTotal() / HISTORY_PAGE_SIZE)));
  private lastRunningExecutionIds = new Set<number>();

  constructor() {
    this.refreshData();

    interval(LIVE_JOBS_POLL_MS)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.refreshLiveJobs());

    interval(CLOCK_TICK_MS)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.now.set(Date.now()));
  }

  refreshData() {
    this.refreshLiveJobs(false);
    this.bakabooru.getJobSchedules().subscribe(data => this.schedules.set(data));
    this.loadHistory();
  }

  loadHistory() {
    this.bakabooru.getJobHistory(HISTORY_PAGE_SIZE, this.historyPage()).subscribe(data => {
      this.history.set(data.items);
      this.historyTotal.set(data.total);
    });
  }

  onHistoryPageChange(page: number) {
    this.historyPage.set(page);
    this.loadHistory();
  }

  runJob(name: string, mode: JobMode) {
    this.bakabooru.startJob(name, mode).subscribe({
      next: () => {
        this.refreshLiveJobs();
      },
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
      this.bakabooru.cancelJob(id).subscribe(() => this.refreshLiveJobs());
    });
  }

  updateSchedule(schedule: ScheduledJob) {
    this.bakabooru.updateJobSchedule(schedule.id, schedule).subscribe({
      next: () => {
        this.toast.success('Schedule updated');
        this.refreshData();
      },
      error: (err) => this.toast.error('Failed to update: ' + err.message)
    });
  }

  getStatusText(status: JobStatus): string {
    return JOB_STATUS_LABELS[status] ?? 'Unknown';
  }

  formatDateTime(value?: string | null): string {
    if (!value) return '-';
    const parsed = dayjs(value);
    return parsed.isValid() ? parsed.format(DATE_TIME_FORMAT) : '-';
  }

  getDuration(start: string, end: string): string {
    const diff = Math.max(0, dayjs(end).diff(dayjs(start), 'second'));

    if (diff < 60) return diff + 's';
    const mins = Math.floor(diff / 60);
    const secs = diff % 60;
    return `${mins}m ${secs}s`;
  }

  getHistoryDuration(run: JobExecution): string {
    if (run.endTime) {
      return this.getDuration(run.startTime, run.endTime);
    }

    if (run.status === JobStatus.Running) {
      return this.getDuration(run.startTime, dayjs(this.now()).toISOString());
    }

    return '-';
  }

  getProgressPercent(state?: JobState): number {
    if (!state || typeof state.processed !== 'number' || typeof state.total !== 'number' || state.total <= 0) {
      return 0;
    }

    return Math.max(0, Math.min(100, (state.processed / state.total) * 100));
  }

  getStateDetail(state?: JobState): string | null {
    if (!state) return null;

    const summary = state.summary?.trim();
    if (summary) return summary;

    const parts: string[] = [];
    if (typeof state.succeeded === 'number') parts.push(`ok ${state.succeeded}`);
    if (typeof state.failed === 'number') parts.push(`failed ${state.failed}`);
    if (typeof state.skipped === 'number') parts.push(`skipped ${state.skipped}`);

    return parts.length > 0 ? parts.join(', ') : null;
  }

  formatHistoryState(run: JobExecution): string {
    const state = run.state;
    if (!state) return '-';

    // While running, prioritize live phase text so it's obvious what the job is doing.
    if (run.status === JobStatus.Running) {
      const phase = state.phase?.trim();
      if (phase) return phase;
    }

    const summary = state.summary?.trim();
    if (summary) return summary;

    const phase = state.phase?.trim();
    if (phase) return phase;

    const parts: string[] = [];
    if (typeof state.succeeded === 'number') parts.push(`ok ${state.succeeded}`);
    if (typeof state.failed === 'number') parts.push(`failed ${state.failed}`);
    if (typeof state.skipped === 'number') parts.push(`skipped ${state.skipped}`);
    if (typeof state.processed === 'number' && typeof state.total === 'number') {
      parts.push(`processed ${state.processed} of ${state.total}`);
    }

    return parts.length > 0 ? parts.join(', ') : '-';
  }

  private refreshLiveJobs(refreshHistoryOnFinished: boolean = true): void {
    this.bakabooru.getJobs().pipe(
      takeUntilDestroyed(this.destroyRef),
      catchError(() =>  [])
    ).subscribe(data => {
      this.jobs.set(data);
      this.patchHistoryWithRunningJobs(data);

      const currentRunningExecutionIds = this.getRunningExecutionIds(data);
      const hasFinishedExecution = this.hasFinishedExecution(this.lastRunningExecutionIds, currentRunningExecutionIds);

      if (refreshHistoryOnFinished && hasFinishedExecution) {
        this.loadHistory();
      }

      this.lastRunningExecutionIds = currentRunningExecutionIds;
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

      return [...missingRunningRows, ...patched].slice(0, HISTORY_PAGE_SIZE);
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

  private hasFinishedExecution(previous: Set<number>, current: Set<number>): boolean {
    if (previous.size === 0) {
      return false;
    }

    for (const executionId of previous) {
      if (!current.has(executionId)) {
        return true;
      }
    }

    return false;
  }
}
