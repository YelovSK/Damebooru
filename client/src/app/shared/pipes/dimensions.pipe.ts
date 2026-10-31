import { Pipe, type PipeTransform } from '@angular/core';

/** "1920×1080", or the fallback while a post's dimensions haven't been extracted yet. */
@Pipe({
  name: 'dimensions',
  standalone: true,
})
export class DimensionsPipe implements PipeTransform {
  transform(item: { width: number | null; height: number | null } | null | undefined, fallback = 'unknown size'): string {
    return item?.width && item.height ? `${item.width}×${item.height}` : fallback;
  }
}
