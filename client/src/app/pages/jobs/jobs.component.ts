import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, interval, of } from 'rxjs';
import { FormsModule } from '@angular/forms';

import { DamebooruService } from '../../services/api/damebooru/damebooru.service';
import { CronPreview, JobExecution, JobKey, JobMode, JobState, JobStatus, JobViewModel, ScheduledJob, KnownJobResult, parseKnownJobResult } from '../../services/api/damebooru/models';
import { ButtonComponent } from '../../shared/components/button/button.component';
import { FormCheckboxComponent } from '../../shared/components/form-checkbox/form-checkbox.component';
import { CollapsibleComponent } from '../../shared/components/collapsible/collapsible.component';
import { FormInputComponent } from '../../shared/components/form-input/form-input.component';
import { PaginatorComponent } from '../../shared/components/paginator/paginator.component';
import { ProgressBarComponent } from '../../shared/components/progress-bar/progress-bar.component';
import { TooltipDirective } from '../../shared/directives';
import { DateTimePipe } from '../../shared/pipes/date-time.pipe';
import { ElapsedDurationPipe } from '../../shared/pipes/elapsed-duration.pipe';
import { RelativeDurationPipe } from '../../shared/pipes/relative-duration.pipe';
import { ToastService } from '../../services/toast.service';
import { ConfirmService } from '../../services/confirm.service';

const LIVE_JOBS_POLL_MS = 2000;
const CLOCK_TICK_MS = 1000;
const HISTORY_PAGE_SIZE = 20;

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
  imports: [CommonModule, FormsModule, ButtonComponent, FormCheckboxComponent, CollapsibleComponent, FormInputComponent, PaginatorComponent, ProgressBarComponent, TooltipDirective, DateTimePipe, ElapsedDurationPipe, RelativeDurationPipe],
  templateUrl: './jobs.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class JobsPageComponent {
  private static readonly CRON_PREVIEW_DEBOUNCE_MS = 350;
  private static readonly CRON_PREVIEW_COUNT = 3;

  private readonly damebooru = inject(DamebooruService);
  private readonly toast = inject(ToastService);
  private readonly confirmService = inject(ConfirmService);
  private readonly destroyRef = inject(DestroyRef);

  readonly jobStatus = JobStatus;
  readonly jobs = signal<JobViewModel[]>([]);
  readonly schedules = signal<ScheduledJob[]>([]);
  readonly cronPreviews = signal<Record<number, CronPreview>>({});
  readonly history = signal<JobExecution[]>([]);
  readonly now = signal(Date.now());
  readonly historyPage = signal(1);
  readonly historyTotal = signal(0);
  readonly historyTotalPages = computed(() => Math.max(1, Math.ceil(this.historyTotal() / HISTORY_PAGE_SIZE)));
  readonly expandedResultExecutionId = signal<number | null>(null);
  cronHelpExpanded = false;
  readonly cronExamples = [
    'Every 6 hours: 0 */6 * * *',
    'Every day at 03:00 UTC: 0 3 * * *',
    'Every Sunday at 03:00 UTC: 0 3 * * 0',
    'Every 15 minutes: */15 * * * *'
  ];

  private lastRunningExecutionIds = new Set<number>();
  private cronPreviewTimers = new Map<number, ReturnType<typeof setTimeout>>();
  private latestCronExpressions = new Map<number, string>();

  constructor() {
    this.refreshData();
    this.destroyRef.onDestroy(() => this.clearCronPreviewTimers());

    interval(LIVE_JOBS_POLL_MS)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.refreshLiveJobs());

    interval(CLOCK_TICK_MS)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.now.set(Date.now()));
  }

  refreshData() {
    this.refreshLiveJobs(false);
    this.damebooru.getJobSchedules().subscribe(data => {
      this.schedules.set(data);
      this.cronPreviews.set({});
      this.clearCronPreviewTimers();

      for (const schedule of data) {
        this.queueCronPreview(schedule.id, schedule.cronExpression, 0);
      }
    });
    this.loadHistory();
  }

  loadHistory() {
    this.damebooru.getJobHistory(HISTORY_PAGE_SIZE, this.historyPage()).subscribe(data => {
      this.history.set(data.items);
      this.historyTotal.set(data.total);
    });
  }

  onHistoryPageChange(page: number) {
    this.historyPage.set(page);
    this.expandedResultExecutionId.set(null);
    this.loadHistory();
  }

  toggleResultDetails(executionId: number): void {
    this.expandedResultExecutionId.update(current => current === executionId ? null : executionId);
  }

  hasResultDetails(run: JobExecution): boolean {
    return this.getKnownResult(run) !== null;
  }

  isResultExpanded(executionId: number): boolean {
    return this.expandedResultExecutionId() === executionId;
  }

  getResultDetailLines(run: JobExecution): string[] {
    const parsed = this.getKnownResult(run);
    if (!parsed) {
      return [];
    }

    switch (parsed.type) {
      case 'scan-all-libraries.v1':
        return [
          `Scanned: ${parsed.data.scanned}`,
          `Added: ${parsed.data.added}`,
          `Updated: ${parsed.data.updated}`,
          `Moved: ${parsed.data.moved}`,
          `Removed: ${parsed.data.removed}`,
        ];
      case 'generate-thumbnails.v1':
        return [
          `Candidates: ${parsed.data.totalCandidates}`,
          `Scanned: ${parsed.data.scanned}`,
          `Generated: ${parsed.data.generated}`,
          `Failed: ${parsed.data.failed}`,
          `Skipped: ${parsed.data.skipped}`,
        ];
      case 'extract-metadata.v1':
        return [
          `Posts: ${parsed.data.totalPosts}`,
          `Processed: ${parsed.data.processed}`,
          `Failed: ${parsed.data.failed}`,
        ];
      case 'compute-similarity.v1':
        return [
          `Candidates: ${parsed.data.totalCandidates}`,
          `Scanned: ${parsed.data.scanned}`,
          `Processed: ${parsed.data.processed}`,
          `Failed: ${parsed.data.failed}`,
        ];
      case 'cleanup-orphaned-thumbnails.v1':
        return [
          `Scanned: ${parsed.data.scanned}`,
          `Deleted: ${parsed.data.deleted}`,
          `Failed: ${parsed.data.failed}`,
        ];
      case 'apply-folder-tags.v1':
        return [
          `Total posts: ${parsed.data.totalPosts}`,
          `Updated posts: ${parsed.data.updatedPosts}`,
          `Added tags: ${parsed.data.addedTags}`,
          `Removed tags: ${parsed.data.removedTags}`,
          `Skipped: ${parsed.data.skipped}`,
          `Failed: ${parsed.data.failed}`,
        ];
      case 'sanitize-tag-names.v1':
        return [
          `Total tags: ${parsed.data.totalTags}`,
          `Processed: ${parsed.data.processed}`,
          `Renamed: ${parsed.data.renamed}`,
          `Merged: ${parsed.data.merged}`,
          `Failed: ${parsed.data.failed}`,
        ];
      case 'find-duplicates.v1':
        return [
          `Groups: ${parsed.data.groups}`,
          `Exact groups: ${parsed.data.exactGroups}`,
          `Perceptual groups: ${parsed.data.perceptualGroups}`,
          `Matched pairs: ${parsed.data.matchedPairs}`,
          `Total entries: ${parsed.data.totalEntries}`,
        ];
      default:
        return [];
    }
  }

  runJob(key: JobKey, mode: JobMode) {
    this.damebooru.startJob(key, mode).subscribe({
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
      this.damebooru.cancelJob(id).subscribe(() => this.refreshLiveJobs());
    });
  }

  updateSchedule(schedule: ScheduledJob) {
    const preview = this.cronPreviews()[schedule.id];
    if (preview && !preview.isValid) {
      this.toast.error(preview.error ?? 'Invalid cron expression.');
      return;
    }

    this.damebooru.updateJobSchedule(schedule.id, schedule).subscribe({
      next: () => {
        this.toast.success('Schedule updated');
        this.refreshData();
      },
      error: (err) => this.toast.error('Failed to update: ' + err.message)
    });
  }

  onCronExpressionChange(schedule: ScheduledJob): void {
    this.queueCronPreview(schedule.id, schedule.cronExpression);
  }

  getCronPreview(scheduleId: number): CronPreview | undefined {
    return this.cronPreviews()[scheduleId];
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

  getStateDetail(state?: JobState): string | null {
    if (!state) return null;

    const finalText = state.finalText?.trim();
    if (finalText) return finalText;

    return null;
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

    const parsedResult = this.getKnownResult(run);
    if (parsedResult?.type === 'scan-all-libraries.v1') {
      const result = parsedResult.data;
      return `Scanned ${result.scanned} files: ${result.added} added, ${result.updated} updated, ${result.moved} moved, ${result.removed} removed`;
    }

    const activityText = state.activityText?.trim();
    if (activityText) return activityText;

    const parts: string[] = [];
    if (typeof state.progressCurrent === 'number' && typeof state.progressTotal === 'number') {
      parts.push(`processed ${state.progressCurrent} of ${state.progressTotal}`);
    }

    return parts.length > 0 ? parts.join(', ') : '-';
  }

  private getKnownResult(run: JobExecution): KnownJobResult | null {
    return parseKnownJobResult(
      run.jobKey,
      run.state?.resultSchemaVersion,
      run.state?.resultJson,
    );
  }

  private refreshLiveJobs(refreshHistoryOnFinished: boolean = true): void {
    this.damebooru.getJobs().pipe(
      takeUntilDestroyed(this.destroyRef),
      catchError(() => of([]))
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

  private queueCronPreview(scheduleId: number, expression: string, delayMs = JobsPageComponent.CRON_PREVIEW_DEBOUNCE_MS): void {
    const normalized = expression.trim();
    this.latestCronExpressions.set(scheduleId, normalized);

    if (this.cronPreviewTimers.has(scheduleId)) {
      clearTimeout(this.cronPreviewTimers.get(scheduleId)!);
    }

    if (!normalized) {
      this.updateCronPreview(scheduleId, {
        isValid: false,
        error: 'Cron expression is required.',
        nextRuns: []
      });
      return;
    }

    const timer = setTimeout(() => {
      this.damebooru.previewCronExpression(normalized, JobsPageComponent.CRON_PREVIEW_COUNT).subscribe({
        next: preview => {
          if (this.latestCronExpressions.get(scheduleId) !== normalized) {
            return;
          }

          this.updateCronPreview(scheduleId, preview);
        },
        error: () => {
          if (this.latestCronExpressions.get(scheduleId) !== normalized) {
            return;
          }

          this.updateCronPreview(scheduleId, {
            isValid: false,
            error: 'Failed to validate cron expression.',
            nextRuns: []
          });
        }
      });
    }, delayMs);

    this.cronPreviewTimers.set(scheduleId, timer);
  }

  private updateCronPreview(scheduleId: number, preview: CronPreview): void {
    this.cronPreviews.update(current => ({
      ...current,
      [scheduleId]: preview
    }));
  }

  private clearCronPreviewTimers(): void {
    for (const timer of this.cronPreviewTimers.values()) {
      clearTimeout(timer);
    }
    this.cronPreviewTimers.clear();
  }

}
