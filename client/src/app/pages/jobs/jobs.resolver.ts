import { inject } from '@angular/core';
import { type ResolveFn } from '@angular/router';
import { forkJoin } from 'rxjs';

import { DamebooruService } from '@services/api/damebooru/damebooru.service';
import { type JobHistoryResponse, type JobViewModel, type ScheduledJob } from '@services/api/damebooru/models';

export const JOB_HISTORY_PAGE_SIZE = 20;

export interface JobsPageData {
  jobs: JobViewModel[];
  schedules: ScheduledJob[];
  history: JobHistoryResponse;
}

export const jobsResolver: ResolveFn<JobsPageData> = () => {
  const damebooru = inject(DamebooruService);
  return forkJoin({
    jobs: damebooru.getJobs(),
    schedules: damebooru.getJobSchedules(),
    history: damebooru.getJobHistory(JOB_HISTORY_PAGE_SIZE, 1),
  });
};
