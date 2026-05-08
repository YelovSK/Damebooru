import { Pipe, type PipeTransform } from '@angular/core';
import { formatBytes } from '@shared/utils/utils';

@Pipe({
    name: 'fileSize',
    standalone: true,
})
export class FileSizePipe implements PipeTransform {
    transform(value: number | null | undefined, decimals = 1): string {
        return formatBytes(value ?? 0, decimals);
    }
}
