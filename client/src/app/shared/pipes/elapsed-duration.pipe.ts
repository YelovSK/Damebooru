import { Pipe, type PipeTransform } from '@angular/core';
import dayjs from 'dayjs/esm';

@Pipe({
  name: 'elapsedDuration',
  standalone: true
})
export class ElapsedDurationPipe implements PipeTransform {
  transform(start?: string | number | Date | null, end?: string | number | Date | null): string {
    if (start == null || end == null) {
      return '-';
    }

    const startTime = dayjs(start);
    const endTime = dayjs(end);
    if (!startTime.isValid() || !endTime.isValid()) {
      return '-';
    }

    const diff = Math.max(0, endTime.diff(startTime, 'second'));
    if (diff < 60) {
      return `${diff}s`;
    }

    const mins = Math.floor(diff / 60);
    const secs = diff % 60;
    return `${mins}m ${secs}s`;
  }
}
