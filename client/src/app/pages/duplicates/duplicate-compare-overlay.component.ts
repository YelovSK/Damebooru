import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, HostListener, computed, effect, inject, input, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { DamebooruService } from '@services/api/damebooru/damebooru.service';
import { DuplicatePostFile } from '@models';
import { ButtonComponent } from '@shared/components/button/button.component';
import { FormDropdownComponent, FormDropdownOption } from '@shared/components/dropdown/form-dropdown.component';
import { ModalComponent } from '@shared/components/modal/modal.component';
import { ZoomPanContainerComponent, ZoomPanViewport } from '@shared/components/zoom-pan-container/zoom-pan-container.component';
import { FileNamePipe } from '@shared/pipes/file-name.pipe';
import { FileSizePipe } from '@shared/pipes/file-size.pipe';
import { getFileNameFromPath } from '@shared/utils/utils';

export type DuplicateCompareMode = 'side-by-side' | 'flip';

export interface DuplicateComparePost {
  id: number;
  relativePath: string;
  width: number;
  height: number;
  sizeBytes: number;
  fileModifiedDate: string;
  thumbnailLibraryId: number;
  thumbnailContentHash: string;
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
    ButtonComponent,
    FormDropdownComponent,
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
  readonly viewport = signal<ZoomPanViewport>({ zoomLevel: 1, panX: 0, panY: 0 });

  readonly activeMode = computed<DuplicateCompareMode>(() => this.isCompactViewport() ? 'flip' : this.mode());

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

  readonly activeFlipPost = computed(() => {
    if (this.flipShowingRight()) {
      return this.selectedRightPost();
    }

    return this.selectedLeftPost();
  });

  readonly selectedLeftValue = computed(() => this.selectedLeftPost()?.id ?? null);

  readonly selectedRightValue = computed(() => this.selectedRightPost()?.id ?? null);

  readonly leftPostOptions = computed<FormDropdownOption<number>[]>(() => this.group().posts.map(post => ({
    label: `#${post.id} · ${getFileNameFromPath(post.relativePath)} · ${post.width}x${post.height} · ${this.formatBytes(post.sizeBytes)}`,
    value: post.id,
    disabled: post.id === this.selectedRightPost()?.id,
  })));

  readonly rightPostOptions = computed<FormDropdownOption<number>[]>(() => this.group().posts.map(post => ({
    label: `#${post.id} · ${getFileNameFromPath(post.relativePath)} · ${post.width}x${post.height} · ${this.formatBytes(post.sizeBytes)}`,
    value: post.id,
    disabled: post.id === this.selectedLeftPost()?.id,
  })));

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

  setLeft(post: DuplicateComparePost): void {
    if (post.id === this.selectedRightPost()?.id) {
      return;
    }

    this.selectedLeftId.set(post.id);
  }

  setRight(post: DuplicateComparePost): void {
    if (post.id === this.selectedLeftPost()?.id) {
      return;
    }

    this.selectedRightId.set(post.id);
  }

  onLeftValueChange(postId: number | null): void {
    const post = this.getPostById(postId);
    if (!post) {
      return;
    }

    this.setLeft(post);
  }

  onRightValueChange(postId: number | null): void {
    const post = this.getPostById(postId);
    if (!post) {
      return;
    }

    this.setRight(post);
  }

  toggleFlipPost(): void {
    this.flipShowingRight.update(value => !value);
  }

  onViewportChange(viewport: ZoomPanViewport): void {
    this.viewport.set(viewport);
  }

  resetZoom(): void {
    this.viewport.set({ zoomLevel: 1, panX: 0, panY: 0 });
  }

  getThumbnailUrl(post: DuplicateComparePost): string {
    return this.damebooru.getThumbnailUrl(post.thumbnailLibraryId, post.thumbnailContentHash);
  }

  getPostContentUrl(post: DuplicateComparePost): string {
    return this.damebooru.getPostContentUrl(post.id);
  }

  isVideo(post: DuplicateComparePost): boolean {
    return post.contentType?.startsWith('video/') ?? false;
  }

  trackPost(_: number, post: DuplicateComparePost): number {
    return post.id;
  }

  private getPostById(postId: number | null): DuplicateComparePost | null {
    if (postId === null) {
      return null;
    }

    return this.group().posts.find(post => post.id === postId) ?? null;
  }

  private formatBytes(bytes: number): string {
    if (!Number.isFinite(bytes) || bytes <= 0) {
      return '0 B';
    }

    const units = ['B', 'KB', 'MB', 'GB'];
    const unitIndex = Math.min(units.length - 1, Math.floor(Math.log(bytes) / Math.log(1024)));
    const value = bytes / Math.pow(1024, unitIndex);
    const precision = unitIndex === 0 || value >= 10 ? 0 : 1;

    return `${value.toFixed(precision)} ${units[unitIndex]}`;
  }

  private resolveIsCompactViewport(): boolean {
    return typeof window !== 'undefined' && window.matchMedia('(max-width: 767px)').matches;
  }
}
