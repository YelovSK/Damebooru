import { Pipe, type PipeTransform } from '@angular/core';
import dayjs from 'dayjs/esm';
import durationPlugin from 'dayjs/esm/plugin/duration';
import relativeTimePlugin from 'dayjs/esm/plugin/relativeTime';

dayjs.extend(durationPlugin);
dayjs.extend(relativeTimePlugin);

@Pipe({
  name: 'relativeDuration',
  standalone: true
})
export class RelativeDurationPipe implements PipeTransform {
  transform(value?: string | null, nowMs?: number): string {
    if (!value) return '-';

    const now = typeof nowMs === 'number' ? nowMs : Date.now();
    const parsed = dayjs(value);
    if (!parsed.isValid()) return '-';

    const diffMs = parsed.diff(now);
    const isFuture = diffMs >= 0;
    const duration = dayjs.duration(Math.abs(diffMs));

    const days = Math.floor(duration.asDays());
    const hours = duration.hours();
    const minutes = duration.minutes();

    const parts: string[] = [];
    if (days > 0) parts.push(this.pluralize(days, 'day'));
    if (hours > 0) parts.push(this.pluralize(hours, 'hour'));
    if (minutes > 0) parts.push(this.pluralize(minutes, 'minute'));

    if (parts.length === 0) {
      return parsed.from(dayjs(now));
    }

    const text = parts.slice(0, 3).join(' ');
    return isFuture ? `In ${text}` : `${text} ago`;
  }

  private pluralize(value: number, unit: string): string {
    return `${value} ${unit}${value === 1 ? '' : 's'}`;
  }
}
