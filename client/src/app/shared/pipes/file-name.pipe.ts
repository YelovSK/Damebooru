import { Pipe, type PipeTransform } from '@angular/core';
import { getFileNameFromPath } from '@shared/utils/utils';

@Pipe({
    name: 'fileName',
    standalone: true,
})
export class FileNamePipe implements PipeTransform {
    transform(value: string | null | undefined): string {
        return getFileNameFromPath(value ?? '');
    }
}
