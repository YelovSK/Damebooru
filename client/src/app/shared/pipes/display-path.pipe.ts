import { Pipe, type PipeTransform } from '@angular/core';
import { formatPathForDisplay } from '@shared/utils/utils';

@Pipe({
    name: 'displayPath',
    standalone: true,
})
export class DisplayPathPipe implements PipeTransform {
    transform(value: string | null | undefined, emptyLabel = ''): string {
        const formatted = formatPathForDisplay(value ?? '');
        return formatted || emptyLabel;
    }
}
