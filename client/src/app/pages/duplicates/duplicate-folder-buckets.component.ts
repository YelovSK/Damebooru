import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ButtonComponent } from '@shared/components/button/button.component';
import { DisplayPathPipe } from '@shared/pipes/display-path.pipe';
import { FileNamePipe } from '@shared/pipes/file-name.pipe';
import { FileSizePipe } from '@shared/pipes/file-size.pipe';

export type DuplicateFolderItemKind = 'exact-file' | 'perceptual-post';
export type DuplicateFolderHoverTone = 'accent' | 'danger';

export interface DuplicateFolderItemView {
  key: string;
  kind: DuplicateFolderItemKind;
  postId: number;
  actionTargetId: number;
  relativePath: string;
  width: number;
  height: number;
  sizeBytes: number;
  thumbnailUrl: string;
  canDelete: boolean;
  isRecommendedKeep: boolean;
}

export interface DuplicateFolderBucketView {
  key: string;
  libraryName: string;
  folderPath: string;
  items: DuplicateFolderItemView[];
}

@Component({
  selector: 'app-duplicate-folder-buckets',
  standalone: true,
  imports: [CommonModule, RouterLink, ButtonComponent, DisplayPathPipe, FileNamePipe, FileSizePipe],
  templateUrl: './duplicate-folder-buckets.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DuplicateFolderBucketsComponent {
  readonly buckets = input.required<DuplicateFolderBucketView[]>();
  readonly hoverTone = input<DuplicateFolderHoverTone>('accent');

  readonly itemMouseEnter = output<number>();
  readonly itemMouseMove = output<number>();
  readonly itemMouseLeave = output<MouseEvent>();
  readonly imageError = output<Event>();
  readonly excludeItem = output<DuplicateFolderItemView>();
  readonly deleteItem = output<DuplicateFolderItemView>();

  trackBucket(_: number, bucket: DuplicateFolderBucketView): string {
    return bucket.key;
  }

  trackItem(_: number, item: DuplicateFolderItemView): string {
    return item.key;
  }
}
