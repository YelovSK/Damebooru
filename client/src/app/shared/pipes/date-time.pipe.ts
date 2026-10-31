import { Pipe, type PipeTransform } from '@angular/core';
import dayjs from 'dayjs/esm';

/** `compact`: "Oct 31, 15:54", with the year only outside the current year. `full`: "2026-10-31 15:54:16", for logs. */
export type DateTimeStyle = 'compact' | 'full';

export function formatDateTime(value?: string | null, style: DateTimeStyle = 'compact', fallback = '-'): string {
  if (!value) return fallback;

  const parsed = dayjs(value);
  if (!parsed.isValid()) return fallback;

  if (style === 'full') return parsed.format('YYYY-MM-DD HH:mm:ss');
  return parsed.format(parsed.year() === dayjs().year() ? 'MMM D, HH:mm' : 'MMM D YYYY, HH:mm');
}

@Pipe({
  name: 'dateTime',
  standalone: true
})
export class DateTimePipe implements PipeTransform {
  transform(value?: string | null, style: DateTimeStyle = 'compact', fallback = '-'): string {
    return formatDateTime(value, style, fallback);
  }
}
