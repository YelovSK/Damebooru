import { Pipe, PipeTransform } from '@angular/core';
import dayjs from 'dayjs/esm';

const DEFAULT_DATE_TIME_FORMAT = 'YYYY-MM-DD HH:mm:ss';

@Pipe({
  name: 'dateTime',
  standalone: true
})
export class DateTimePipe implements PipeTransform {
  transform(value?: string | null, format: string = DEFAULT_DATE_TIME_FORMAT, fallback = '-'): string {
    if (!value) return fallback;

    const parsed = dayjs(value);
    return parsed.isValid() ? parsed.format(format) : fallback;
  }
}
