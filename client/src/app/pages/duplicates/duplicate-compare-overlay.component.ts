import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, HostListener, computed, effect, inject, input, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CdkMenu, CdkMenuItem, CdkMenuTrigger } from '@angular/cdk/menu';
import { DamebooruService } from '@services/api/damebooru/damebooru.service';
import { type DuplicatePostFile } from '@models';
import { ButtonDirective, TooltipDirective } from '@shared/directives';
import { ModalComponent } from '@shared/components/modal/modal.component';
import { ZoomPanContainerComponent, ZoomPanSync } from '@shared/components/zoom-pan-container/zoom-pan-container.component';
import { FileNamePipe } from '@shared/pipes/file-name.pipe';
import { FileSizePipe } from '@shared/pipes/file-size.pipe';
import { formatPathForDisplay, getFileFormat } from '@shared/utils/utils';

export type DuplicateCompareMode = 'side-by-side' | 'flip';

export interface DuplicateComparePost {
  id: number;
  relativePath: string;
  width: number;
  height: number;
  sizeBytes: number;
  fileModifiedDate: string;
  contentHash: string;
  isRecommendedKeep: boolean;
  files: DuplicatePostFile[];
  contentType?: string;
}

export interface DuplicateCompareGroup {
  key: string;
  duplicateGroupId: number;
  similarityPercent: number | null;
  posts: DuplicateComparePost[];
}

@Component({
  selector: 'app-duplicate-compare-overlay',
  standalone: true,
  imports: [
    CommonModule,
    RouterLink,
    CdkMenu,
    CdkMenuItem,
    CdkMenuTrigger,
    ButtonDirective,
    TooltipDirective,
    ModalComponent,
    ZoomPanContainerComponent,
    FileNamePipe,
    FileSizePipe,
  ],
  templateUrl: './duplicate-compare-overlay.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DuplicateCompareOverlayComponent {
  private readonly damebooru = inject(DamebooruService);

  readonly group = input.required<DuplicateCompareGroup>();

  readonly closed = output<void>();
  readonly excludePost = output<DuplicateComparePost>();
  readonly deletePost = output<DuplicateComparePost>();

  readonly selectedLeftId = signal<number | null>(null);
  readonly selectedRightId = signal<number | null>(null);
  readonly mode = signal<DuplicateCompareMode>('side-by-side');
  readonly isCompactViewport = signal(this.resolveIsCompactViewport());
  readonly flipShowingRight = signal(false);
  readonly zoomSync = new ZoomPanSync();

  readonly activeMode = computed<DuplicateCompareMode>(() => this.isCompactViewport() ? 'flip' : this.mode());
  /** With only two posts there is nothing to pick, so the panels show plain names instead of selectors. */
  readonly canChoosePosts = computed(() => this.group().posts.length > 2);

  readonly selectedLeftPost = computed(() => {
    const group = this.group();
    return group.posts.find(post => post.id === this.selectedLeftId()) ?? group.posts[0] ?? null;
  });

  readonly selectedRightPost = computed(() => {
    const group = this.group();
    const left = this.selectedLeftPost();
    const selected = group.posts.find(post => post.id === this.selectedRightId());
    if (selected && selected.id !== left?.id) {
      return selected;
    }

    return group.posts.find(post => post.id !== left?.id) ?? left ?? null;
  });

  readonly activeFlipPost = computed(() => this.flipShowingRight() ? this.selectedRightPost() : this.selectedLeftPost());

  constructor() {
    effect(() => {
      const posts = this.group().posts;
      this.selectedLeftId.set(posts[0]?.id ?? null);
      this.selectedRightId.set(posts.find(post => post.id !== posts[0]?.id)?.id ?? posts[0]?.id ?? null);
      this.flipShowingRight.set(false);
      this.resetZoom();
    });
  }

  close(): void {
    this.closed.emit();
  }

  setMode(mode: DuplicateCompareMode): void {
    this.mode.set(mode);
  }

  @HostListener('window:resize')
  onWindowResize(): void {
    this.isCompactViewport.set(this.resolveIsCompactViewport());
  }

  @HostListener('document:keydown.space', ['$event'])
  onSpace(event: Event): void {
    if (this.activeMode() !== 'flip' || (event.target as HTMLElement | null)?.closest('input, textarea, [contenteditable]')) {
      return;
    }

    event.preventDefault();
    this.flipShowingRight.update(value => !value);
  }

  selectPost(side: 'left' | 'right', post: DuplicateComparePost): void {
    const other = side === 'left' ? this.selectedRightPost() : this.selectedLeftPost();
    if (post.id === other?.id) {
      return;
    }

    (side === 'left' ? this.selectedLeftId : this.selectedRightId).set(post.id);
  }

  resetZoom(): void {
    this.zoomSync.publish({ zoomLevel: 1, panX: 0, panY: 0 });
  }

  getPostContentUrl(post: DuplicateComparePost): string {
    return this.damebooru.getPostContentUrl(post.id);
  }

  isVideo(post: DuplicateComparePost): boolean {
    return post.contentType?.startsWith('video/') ?? false;
  }

  getFormat(post: DuplicateComparePost): string {
    return getFileFormat(post.relativePath, post.contentType);
  }

  getFolder(post: DuplicateComparePost): string {
    const separator = Math.max(post.relativePath.lastIndexOf('/'), post.relativePath.lastIndexOf('\\'));
    return formatPathForDisplay(separator < 0 ? '' : post.relativePath.slice(0, separator)) || '(library root)';
  }

  hasMorePixels(post: DuplicateComparePost, other: DuplicateComparePost | null): boolean {
    return !!other && post.width * post.height > other.width * other.height;
  }

  private resolveIsCompactViewport(): boolean {
    return typeof window !== 'undefined' && window.matchMedia('(max-width: 767px)').matches;
  }
}
