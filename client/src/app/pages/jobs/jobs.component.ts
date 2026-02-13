import { Component, OnInit, OnDestroy, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { JobService, JobViewModel, JobExecution, ScheduledJob } from '../../services/api/job.service';
import { Subscription, interval } from 'rxjs';
import { FormsModule } from '@angular/forms';
import { ButtonComponent } from '../../shared/components/button/button.component';
import { PaginatorComponent } from '../../shared/components/paginator/paginator.component';
import { ProgressBarComponent } from '../../shared/components/progress-bar/progress-bar.component';
import { ToastService } from '../../services/toast.service';
import { ConfirmService } from '../../services/confirm.service';

const JOB_DESCRIPTIONS: Record<string, string> = {
  'Scan All Libraries': 'Discovers new files and creates post records. Fast — only computes content hashes.',
  'Generate Thumbnails': 'Creates thumbnail images for posts.',
  'Cleanup Orphaned Thumbnails': 'Deletes thumbnail files that no longer belong to any post.',
  'Extract Metadata': 'Reads image dimensions and content type for posts.',
  'Compute Similarity': 'Computes perceptual hashes (dHash) for duplicate detection.',
  'Find Duplicates': 'Finds exact (content hash) and perceptual (dHash) duplicate post groups.'
};

/** Jobs that make sense with Missing/All modes */
const MODAL_JOBS = new Set(['Generate Thumbnails', 'Extract Metadata', 'Compute Similarity']);

@Component({
  selector: 'app-jobs-page',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonComponent, PaginatorComponent, ProgressBarComponent],
  template: `
    <div class="container mx-auto p-6">
      <h1 class="text-3xl font-bold mb-2 text-terminal-green">Job Queues</h1>
      <p class="text-gray-400 mb-8">Run and schedule background processing tasks.</p>

      <!-- Job Cards Grid -->
      <div class="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-5 mb-10">
        <div *ngFor="let job of jobs()" class="bg-gray-900 border border-gray-700 rounded-lg p-5 shadow-lg flex flex-col">
          <!-- Header -->
          <div class="flex justify-between items-start mb-2">
            <h3 class="text-lg font-semibold text-white">{{ job.name }}</h3>
            <span *ngIf="job.isRunning"
                  class="bg-yellow-600/20 text-yellow-400 border border-yellow-600/40 px-2 py-0.5 rounded text-xs font-bold shrink-0 ml-2">
              RUNNING
            </span>
          </div>
          <p class="text-sm text-gray-400 mb-4 flex-1">{{ getDescription(job.name) }}</p>

          <!-- Progress Bar (when running) -->
          <div *ngIf="job.isRunning && job.activeJobInfo" class="mb-4">
            <app-progress-bar class="mb-1.5" [progress]="job.activeJobInfo.progress" [trackClass]="'bg-gray-700'" [fillClass]="'bg-accent-primary'"></app-progress-bar>
            <p class="text-xs text-gray-400">{{ job.activeJobInfo.message }} ({{ getProgressPercent(job.activeJobInfo.progress) | number:'1.0-0' }}%)</p>
          </div>

          <!-- Action Buttons -->
          <div class="flex gap-2 mt-auto">
            <ng-container *ngIf="!job.isRunning">
              <ng-container *ngIf="hasModalModes(job.name); else singleButton">
                <app-button variant="primary" size="sm" [fullWidth]="true" (click)="runJob(job.name, 'missing')">Missing</app-button>
                <app-button variant="secondary" size="sm" [fullWidth]="true" (click)="runJob(job.name, 'all')">All</app-button>
              </ng-container>
              <ng-template #singleButton>
                <app-button variant="primary" size="sm" [fullWidth]="true" (click)="runJob(job.name, 'missing')">Run</app-button>
              </ng-template>
            </ng-container>
            <app-button *ngIf="job.isRunning && job.activeJobInfo" variant="danger" size="sm" [fullWidth]="true" (click)="cancelJob(job.activeJobInfo.id)">Cancel</app-button>
          </div>
        </div>
      </div>

      <!-- Schedules -->
      <h2 class="text-2xl font-bold mb-4 text-white">Schedules</h2>
      <div class="bg-gray-900 border border-gray-700 rounded-lg p-6 shadow-lg mb-8">
        <table class="w-full text-left text-gray-300">
          <thead>
            <tr class="border-b border-gray-700">
              <th class="py-2">Job Name</th>
              <th class="py-2">Cron Expression</th>
              <th class="py-2">Next Run</th>
              <th class="py-2">Last Run</th>
              <th class="py-2">Enabled</th>
              <th class="py-2">Actions</th>
            </tr>
          </thead>
          <tbody>
            <tr *ngFor="let schedule of schedules()" class="border-b border-gray-800">
              <td class="py-2">{{schedule.jobName}}</td>
              <td class="py-2">
                <input type="text" [(ngModel)]="schedule.cronExpression"
                       placeholder="0 */6 * * *"
                       class="bg-gray-800 border border-gray-600 rounded px-2 py-1 text-white w-36 font-mono text-sm">
              </td>
              <td class="py-2 text-sm">{{schedule.nextRun | date:'medium'}}</td>
              <td class="py-2 text-sm">{{schedule.lastRun | date:'medium'}}</td>
              <td class="py-2">
                <input type="checkbox" [(ngModel)]="schedule.isEnabled" class="w-4 h-4">
              </td>
              <td class="py-2">
                <app-button variant="secondary" size="xs" (click)="updateSchedule(schedule)">Save</app-button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>

      <!-- History -->
      <h2 class="text-2xl font-bold mb-4 text-white">Execution History</h2>
      <div class="bg-gray-900 border border-gray-700 rounded-lg p-6 shadow-lg overflow-x-auto">
        <table class="w-full text-left text-gray-300">
          <thead>
            <tr class="border-b border-gray-700">
              <th class="py-2">Job</th>
              <th class="py-2">Status</th>
              <th class="py-2">Start Time</th>
              <th class="py-2">Duration</th>
              <th class="py-2">Error</th>
            </tr>
          </thead>
          <tbody>
            <tr *ngFor="let run of history()" class="border-b border-gray-800 hover:bg-gray-800">
              <td class="py-2">{{run.jobName}}</td>
              <td class="py-2">
                <span [ngClass]="{
                  'text-green-400': run.status == 2,
                  'text-red-400': run.status == 3,
                  'text-yellow-400': run.status == 1,
                  'text-gray-400': run.status == 4
                }">
                  {{ getStatusText(run.status) }}
                </span>
              </td>
              <td class="py-2">{{run.startTime | date:'medium'}}</td>
              <td class="py-2">
                {{ getHistoryDuration(run) }}
              </td>
              <td class="py-2 text-red-400 text-sm">{{run.errorMessage}}</td>
            </tr>
          </tbody>
        </table>

        <!-- Paginator -->
        <div *ngIf="historyTotalPages() > 1" class="flex justify-center mt-4 pt-4 border-t border-gray-700">
          <app-paginator
            [currentPage]="historyPage()"
            [totalPages]="historyTotalPages()"
            (pageChange)="onHistoryPageChange($event)">
          </app-paginator>
        </div>
      </div>
    </div>
  `
})
export class JobsPageComponent implements OnInit, OnDestroy {
  jobs = signal<JobViewModel[]>([]);
  schedules = signal<ScheduledJob[]>([]);
  history = signal<JobExecution[]>([]);
  now = signal(Date.now());
  historyPage = signal(1);
  historyTotal = signal(0);
  historyPageSize = 20;
  historyTotalPages = computed(() => Math.max(1, Math.ceil(this.historyTotal() / this.historyPageSize)));
  private pollSub?: Subscription;
  private clockSub?: Subscription;

  constructor(
    private jobService: JobService,
    private toast: ToastService,
    private confirmService: ConfirmService
  ) { }

  ngOnInit() {
    this.refreshData();
    // Poll for active jobs AND history every 2 seconds
    this.pollSub = interval(2000).subscribe(() => {
      this.jobService.getJobs().subscribe(data => this.jobs.set(data));
      this.loadHistory();
    });

    this.clockSub = interval(1000).subscribe(() => {
      this.now.set(Date.now());
    });
  }

  ngOnDestroy() {
    this.pollSub?.unsubscribe();
    this.clockSub?.unsubscribe();
  }

  refreshData() {
    this.jobService.getJobs().subscribe(data => this.jobs.set(data));
    this.jobService.getSchedules().subscribe(data => this.schedules.set(data));
    this.loadHistory();
  }

  loadHistory() {
    this.jobService.getHistory(this.historyPageSize, this.historyPage()).subscribe(data => {
      this.history.set(data.items);
      this.historyTotal.set(data.total);
    });
  }

  onHistoryPageChange(page: number) {
    this.historyPage.set(page);
    this.loadHistory();
  }

  getDescription(name: string): string {
    return JOB_DESCRIPTIONS[name] || '';
  }

  hasModalModes(name: string): boolean {
    return MODAL_JOBS.has(name);
  }

  runJob(name: string, mode: 'missing' | 'all') {
    this.jobService.startJob(name, mode).subscribe({
      next: () => this.refreshData(),
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
      this.jobService.cancelJob(id).subscribe(() => this.refreshData());
    });
  }

  updateSchedule(schedule: ScheduledJob) {
    this.jobService.updateSchedule(schedule.id, schedule).subscribe({
      next: () => {
        this.toast.success('Schedule updated');
        this.refreshData();
      },
      error: (err) => this.toast.error('Failed to update: ' + err.message)
    });
  }

  getStatusText(status: number): string {
    switch (status) {
      case 0: return 'Idle';
      case 1: return 'Running';
      case 2: return 'Completed';
      case 3: return 'Failed';
      case 4: return 'Cancelled';
      default: return 'Unknown';
    }
  }

  getDuration(start: string, end: string): string {
    const s = new Date(start).getTime();
    const e = new Date(end).getTime();
    const diff = Math.round((e - s) / 1000);

    if (diff < 60) return diff + 's';
    const mins = Math.floor(diff / 60);
    const secs = diff % 60;
    return `${mins}m ${secs}s`;
  }

  getHistoryDuration(run: JobExecution): string {
    if (run.endTime) {
      return this.getDuration(run.startTime, run.endTime);
    }

    if (run.status === 1) {
      return this.getDuration(run.startTime, new Date(this.now()).toISOString());
    }

    return '-';
  }

  getProgressPercent(progress: number): number {
    if (!Number.isFinite(progress)) return 0;

    // Be tolerant if backend ever reports 0..1 instead of 0..100.
    const normalized = progress <= 1 ? progress * 100 : progress;
    return Math.max(0, Math.min(100, normalized));
  }
}
