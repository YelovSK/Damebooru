import { ChangeDetectionStrategy, Component, HostListener, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { DamebooruService } from '@services/api/damebooru/damebooru.service';
import {
  DuplicateType,
  DamebooruPostDto,
  DuplicateGroup,
  DuplicatePost,
  ExcludedFile,
  ResolveSameFolderGroupRequest,
  SameFolderDuplicateGroup,
  SameFolderDuplicatePost
} from '@models';
import { FileNamePipe } from '@shared/pipes/file-name.pipe';
import { FileSizePipe } from '@shared/pipes/file-size.pipe';
import { getFileNameFromPath } from '@shared/utils/utils';
import { ConfirmService } from '@services/confirm.service';
import { ToastService } from '@services/toast.service';
import { TabsComponent } from '@shared/components/tabs/tabs.component';
import { TabComponent } from '@shared/components/tabs/tab.component';
import { ButtonComponent } from '@shared/components/button/button.component';
import { FormCheckboxComponent } from '@shared/components/form-checkbox/form-checkbox.component';
import { PaginatorComponent } from '@shared/components/paginator/paginator.component';
import { PostPreviewOverlayComponent } from '@shared/components/post-preview-overlay/post-preview-overlay.component';
import { SettingsService } from '@services/settings.service';

type DuplicateScope = 'all' | 'same-folder';

interface VisibleDuplicatePost {
  id: number;
  relativePath: string;
  width: number;
  height: number;
  sizeBytes: number;
  fileModifiedDate: string;
  thumbnailLibraryId: number;
  thumbnailContentHash: string;
  isRecommendedKeep: boolean;
}

interface VisibleDuplicateGroup {
  key: string;
  scope: DuplicateScope;
  duplicateGroupId: number;
  type: DuplicateType;
  similarityPercent: number | null;
  detectedDate: string | null;
  libraryName: string | null;
  folderPath: string | null;
  sameFolderLibraryId: number | null;
  posts: VisibleDuplicatePost[];
}

@Component({
  selector: 'app-duplicates-page',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, FileNamePipe, FileSizePipe, TabsComponent, TabComponent, ButtonComponent, FormCheckboxComponent, PaginatorComponent, PostPreviewOverlayComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './duplicates.component.html',
})
export class DuplicatesPageComponent {
  private readonly damebooru = inject(DamebooruService);
  private readonly confirmService = inject(ConfirmService);
  private readonly toast = inject(ToastService);
  private readonly settingsService = inject(SettingsService);
  readonly duplicateType = DuplicateType;
  readonly hoverPreviewEnabled = this.settingsService.enablePostPreviewOnHover;
  readonly hoverPreviewDelayMs = computed(() => {
    const raw = this.settingsService.postPreviewDelayMs();
    if (!Number.isFinite(raw)) {
      return 700;
    }

    return Math.max(0, Math.min(5000, Math.round(raw)));
  });

  groups = signal<DuplicateGroup[]>([]);
  exactCount = signal(0);
  perceptualCount = signal(0);

  resolvedGroups = signal<DuplicateGroup[]>([]);

  excludedFiles = signal<ExcludedFile[]>([]);

  sameFolderGroups = signal<SameFolderDuplicateGroup[]>([]);

  showSameFolderOnly = signal(false);
  previewPost = signal<DamebooruPostDto | null>(null);
  readonly visiblePageSize = 25;
  visiblePage = signal(1);
  readonly resolvedPageSize = 25;
  resolvedPage = signal(1);
  private groupsTabInitialized = false;
  private resolvedTabInitialized = false;
  private excludedTabInitialized = false;
  private hoverPreviewTimer: ReturnType<typeof setTimeout> | null = null;
  private hoveredPostId: number | null = null;
  private readonly previewPostCache = new Map<number, DamebooruPostDto>();

  onGroupsTabInit() {
    if (this.groupsTabInitialized) {
      return;
    }

    this.groupsTabInitialized = true;
    this.loadGroups();
    this.loadSameFolderGroups();
  }

  onResolvedTabInit() {
    if (this.resolvedTabInitialized) {
      return;
    }

    this.resolvedTabInitialized = true;
    this.loadResolvedGroups();
  }

  onExcludedTabInit() {
    if (this.excludedTabInitialized) {
      return;
    }

    this.excludedTabInitialized = true;
    this.loadExcludedFiles();
  }

  onSameFolderOnlyChange(checked: boolean) {
    this.showSameFolderOnly.set(checked);
    this.visiblePage.set(1);
  }

  readonly visibleGroups = computed<VisibleDuplicateGroup[]>(() => {
    if (this.showSameFolderOnly()) {
      return this.sameFolderGroups().map(group => ({
        key: `${group.parentDuplicateGroupId}:${group.libraryId}:${group.folderPath}`,
        scope: 'same-folder',
        duplicateGroupId: group.parentDuplicateGroupId,
        type: group.duplicateType,
        similarityPercent: group.similarityPercent,
        detectedDate: null,
        libraryName: group.libraryName,
        folderPath: group.folderPath,
        sameFolderLibraryId: group.libraryId,
        posts: group.posts.map(post => ({
          id: post.id,
          relativePath: post.relativePath,
          width: post.width,
          height: post.height,
          sizeBytes: post.sizeBytes,
          fileModifiedDate: post.fileModifiedDate,
          thumbnailLibraryId: post.thumbnailLibraryId,
          thumbnailContentHash: post.thumbnailContentHash,
          isRecommendedKeep: post.id === group.recommendedKeepPostId,
        })),
      }));
    }

    return this.groups().map(group => {
      const recommendedKeepPostId = this.selectBestQualityVisiblePostId(group.posts);

      return {
        key: `${group.id}`,
        scope: 'all',
        duplicateGroupId: group.id,
        type: group.type,
        similarityPercent: group.similarityPercent,
        detectedDate: group.detectedDate,
        libraryName: null,
        folderPath: null,
        sameFolderLibraryId: null,
        posts: group.posts.map(post => ({
          id: post.id,
          relativePath: post.relativePath,
          width: post.width,
          height: post.height,
          sizeBytes: post.sizeBytes,
          fileModifiedDate: post.fileModifiedDate,
          thumbnailLibraryId: post.thumbnailLibraryId,
          thumbnailContentHash: post.thumbnailContentHash,
          isRecommendedKeep: recommendedKeepPostId !== null && post.id === recommendedKeepPostId,
        })),
      };
    });
  });

  readonly visibleStats = computed(() => {
    const groups = this.visibleGroups();
    let postCount = 0;
    let exactCount = 0;
    let perceptualCount = 0;

    for (const group of groups) {
      postCount += group.posts.length;
      if (group.type === DuplicateType.Exact) {
        exactCount++;
      } else {
        perceptualCount++;
      }
    }

    return {
      groupCount: groups.length,
      postCount,
      exactCount,
      perceptualCount,
    };
  });

  readonly visibleTotalPages = computed(() =>
    Math.max(1, Math.ceil(this.visibleStats().groupCount / this.visiblePageSize)));

  readonly visibleCurrentPage = computed(() =>
    Math.min(this.visiblePage(), this.visibleTotalPages()));

  readonly pagedVisibleGroups = computed(() => {
    const start = (this.visibleCurrentPage() - 1) * this.visiblePageSize;
    return this.visibleGroups().slice(start, start + this.visiblePageSize);
  });

  readonly resolvedTotalPages = computed(() =>
    Math.max(1, Math.ceil(this.resolvedGroups().length / this.resolvedPageSize)));

  readonly resolvedCurrentPage = computed(() =>
    Math.min(this.resolvedPage(), this.resolvedTotalPages()));

  readonly pagedResolvedGroups = computed(() => {
    const start = (this.resolvedCurrentPage() - 1) * this.resolvedPageSize;
    return this.resolvedGroups().slice(start, start + this.resolvedPageSize);
  });

  getVisibleGroups(): VisibleDuplicateGroup[] {
    return this.visibleGroups();
  }

  getVisibleGroupCount(): number {
    return this.visibleStats().groupCount;
  }

  getVisiblePostCount(): number {
    return this.visibleStats().postCount;
  }

  getVisibleExactCount(): number {
    return this.visibleStats().exactCount;
  }

  getVisiblePerceptualCount(): number {
    return this.visibleStats().perceptualCount;
  }

  canBulkResolveVisible(): boolean {
    return this.visibleStats().groupCount > 0;
  }

  getBulkResolveButtonLabel(): string {
    return `Auto Resolve All (${this.visibleStats().groupCount} groups)`;
  }

  getBulkResolveButtonClass(): string {
    return this.showSameFolderOnly()
      ? 'bg-red-600 hover:bg-red-700 text-white font-bold py-2 px-6 rounded transition-colors'
      : 'bg-blue-600 hover:bg-blue-700 text-white font-bold py-2 px-6 rounded transition-colors';
  }

  getBulkResolveHint(): string {
    return this.showSameFolderOnly()
      ? 'Keeps highest-quality post and deletes others from disk'
      : 'Keeps highest-quality post and removes others from booru';
  }

  isSameFolderScope(group: VisibleDuplicateGroup): boolean {
    return group.scope === 'same-folder';
  }

  getFolderDisplayPath(group: VisibleDuplicateGroup): string {
    if (!group.folderPath) {
      return '(library root)';
    }

    return group.folderPath;
  }

  resolveAllVisible() {
    if (this.showSameFolderOnly()) {
      this.resolveAllSameFolder();
      return;
    }

    this.resolveAllGroups();
  }

  onAutoResolveGroup(group: VisibleDuplicateGroup) {
    if (group.scope === 'same-folder') {
      const request: ResolveSameFolderGroupRequest = {
        parentDuplicateGroupId: group.duplicateGroupId,
        libraryId: group.sameFolderLibraryId!,
        folderPath: group.folderPath ?? '',
      };

      this.confirmService.confirm({
        title: 'Auto Resolve Group',
        message: `Auto-resolve this same-folder group by keeping the highest-quality post and deleting the rest from disk?`,
        confirmText: 'Auto Resolve',
        variant: 'danger',
      }).subscribe(confirmed => {
        if (!confirmed) return;

        this.damebooru.resolveSameFolderGroup(request).subscribe({
          next: (result) => {
            this.reloadDuplicatesAndExcluded();
            this.toast.success(`Resolved ${result.resolvedGroups} group(s), deleted ${result.deletedPosts} post(s), skipped ${result.skippedGroups}.`);
          },
          error: () => this.toast.error('Failed to auto-resolve same-folder group.')
        });
      });

      return;
    }

    this.confirmService.confirm({
      title: 'Auto Resolve Group',
      message: `Auto-resolve this group by keeping the highest-quality post and removing the others from the booru?`,
      confirmText: 'Auto Resolve',
      variant: 'danger',
    }).subscribe(confirmed => {
      if (!confirmed) return;

      this.damebooru.autoResolveGroup(group.duplicateGroupId).subscribe({
        next: () => {
          this.reloadDuplicatesAndExcluded();
          this.toast.success('Duplicate group auto-resolved.');
        },
        error: () => this.toast.error('Failed to auto-resolve duplicate group.')
      });
    });
  }

  onDismissGroup(group: VisibleDuplicateGroup) {
    if (group.scope === 'same-folder') {
      return;
    }

    this.confirmService.confirm({
      title: 'Dismiss Group',
      message: `Dismiss this group? All ${group.posts.length} posts will remain in the booru and on disk.`,
      confirmText: 'Dismiss',
      variant: 'warning',
    }).subscribe(confirmed => {
      if (!confirmed) return;

      this.damebooru.keepAllInGroup(group.duplicateGroupId).subscribe({
        next: () => {
          this.reloadDuplicatesAndExcluded();
          this.toast.success('Duplicate group dismissed.');
        },
        error: () => this.toast.error('Failed to dismiss duplicate group.')
      });
    });
  }

  onExcludePost(group: VisibleDuplicateGroup, post: VisibleDuplicatePost) {
    this.confirmService.confirm({
      title: 'Exclude Post',
      message: `Exclude "${getFileNameFromPath(post.relativePath)}" from the booru? The file will remain on disk and will not be imported again.`,
      confirmText: 'Exclude',
      variant: 'warning',
    }).subscribe(confirmed => {
      if (!confirmed) return;

      this.damebooru.excludePostFromGroup(group.duplicateGroupId, post.id).subscribe({
        next: () => {
          this.reloadDuplicatesAndExcluded();
          this.toast.success('Duplicate post excluded.');
        },
        error: () => this.toast.error('Failed to exclude duplicate post.')
      });
    });
  }

  onDeletePost(group: VisibleDuplicateGroup, post: VisibleDuplicatePost) {
    if (group.scope !== 'same-folder') {
      return;
    }

    this.confirmService.confirm({
      title: 'Delete Post',
      message: `Delete "${getFileNameFromPath(post.relativePath)}" from disk and remove it from the booru?`,
      confirmText: 'Delete',
      variant: 'danger',
    }).subscribe(confirmed => {
      if (!confirmed) return;

      this.damebooru.deletePostFromGroup(group.duplicateGroupId, post.id).subscribe({
        next: () => {
          this.reloadDuplicatesAndExcluded();
          this.toast.success('Duplicate post deleted.');
        },
        error: () => this.toast.error('Failed to delete duplicate post.')
      });
    });
  }

  getResolvedGroupCount(): number {
    return this.resolvedGroups().length;
  }

  getResolvedPostCount(): number {
    return this.resolvedGroups().reduce((sum, group) => sum + group.posts.length, 0);
  }

  getResolvedExactCount(): number {
    return this.resolvedGroups().filter(group => group.type === DuplicateType.Exact).length;
  }

  getResolvedPerceptualCount(): number {
    return this.resolvedGroups().filter(group => group.type === DuplicateType.Perceptual).length;
  }

  loadResolvedGroups() {
    this.damebooru.getResolvedDuplicateGroups().subscribe({
      next: (groups) => {
        this.resolvedGroups.set(groups);
        this.resolvedPage.set(Math.min(this.resolvedPage(), this.resolvedTotalPages()));
      },
      error: () => {}
    });
  }

  unresolveGroup(group: DuplicateGroup) {
    this.confirmService.confirm({
      title: 'Mark Group Unresolved',
      message: `Move this group back to unresolved duplicates for review?`,
      confirmText: 'Mark Unresolved',
      variant: 'warning',
    }).subscribe(confirmed => {
      if (!confirmed) return;

      this.damebooru.markDuplicateGroupUnresolved(group.id).subscribe({
        next: () => {
          this.reloadDuplicatesAndExcluded();
          this.toast.success('Group marked as unresolved.');
        },
        error: () => this.toast.error('Failed to mark group as unresolved.')
      });
    });
  }

  unresolveAllGroups() {
    const count = this.getResolvedGroupCount();
    if (count === 0) {
      return;
    }

    this.confirmService.confirm({
      title: 'Mark All Unresolved',
      message: `Move all ${count} resolved groups back to unresolved duplicates?`,
      confirmText: 'Mark All Unresolved',
      variant: 'warning',
    }).subscribe(confirmed => {
      if (!confirmed) return;

      this.damebooru.markAllDuplicateGroupsUnresolved().subscribe({
        next: (result) => {
          this.reloadDuplicatesAndExcluded();
          this.toast.success(`Marked ${result.unresolved} group(s) as unresolved.`);
        },
        error: () => this.toast.error('Failed to mark all groups as unresolved.')
      });
    });
  }

  trackVisibleGroup(_: number, group: VisibleDuplicateGroup) {
    return group.key;
  }

  trackVisiblePost(_: number, post: VisibleDuplicatePost) {
    return post.id;
  }

  onVisiblePageChange(page: number) {
    this.visiblePage.set(page);
  }

  onResolvedPageChange(page: number) {
    this.resolvedPage.set(page);
  }

  loadGroups() {
    this.damebooru.getDuplicateGroups().subscribe({
      next: (groups) => {
        this.groups.set(groups);
        this.visiblePage.set(Math.min(this.visiblePage(), this.visibleTotalPages()));
        this.exactCount.set(groups.filter(group => group.type === DuplicateType.Exact).length);
        this.perceptualCount.set(groups.filter(group => group.type === DuplicateType.Perceptual).length);
      },
      error: () => {}
    });
  }

  loadSameFolderGroups() {
    this.damebooru.getSameFolderDuplicateGroups().subscribe({
      next: (groups) => {
        this.sameFolderGroups.set(groups);
        this.visiblePage.set(Math.min(this.visiblePage(), this.visibleTotalPages()));
      },
      error: () => {}
    });
  }

  resolveAllExact() {
    if (this.showSameFolderOnly()) {
      this.resolveAllSameFolderExact();
      return;
    }

    const count = this.exactCount();
    this.confirmService.confirm({
      title: 'Resolve Exact Duplicates',
      message: `Auto-resolve all ${count} exact groups? This keeps the highest-quality post and removes the rest from booru.`,
      confirmText: 'Resolve All',
      variant: 'danger',
    }).subscribe(confirmed => {
      if (!confirmed) return;

      this.damebooru.resolveAllExactDuplicates().subscribe({
        next: (result) => {
          this.reloadDuplicatesAndExcluded();
          this.toast.success(`Auto-resolved ${result.resolved} exact duplicate groups.`);
        },
        error: (err) => this.toast.error('Failed: ' + (err?.message || 'Unknown error'))
      });
    });
  }

  resolveAllGroups() {
    const count = this.getVisibleGroupCount();
    this.confirmService.confirm({
      title: 'Resolve Duplicate Groups',
      message: `Auto-resolve all ${count} visible duplicate groups? This keeps the highest-quality post and removes the others from booru.`,
      confirmText: 'Resolve All',
      variant: 'danger',
    }).subscribe(confirmed => {
      if (!confirmed) return;

      this.damebooru.resolveAllDuplicateGroups().subscribe({
        next: (result) => {
          this.reloadDuplicatesAndExcluded();
          this.toast.success(`Auto-resolved ${result.resolved} duplicate groups.`);
        },
        error: (err) => this.toast.error('Failed: ' + (err?.message || 'Unknown error'))
      });
    });
  }

  resolveAllSameFolder() {
    const count = this.sameFolderGroups().length;
    this.confirmService.confirm({
      title: 'Resolve Same-Folder Duplicates',
      message: `Auto-resolve all ${count} same-folder groups? This keeps the highest-quality post and deletes the rest from disk.`,
      confirmText: 'Resolve All',
      variant: 'danger',
    }).subscribe(confirmed => {
      if (!confirmed) return;

      this.confirmService.confirm({
        title: 'Confirm File Deletion',
        message: 'This will DELETE files from disk. Do you REALLY want to delete all files in the selected duplicate groups?',
        confirmText: 'Yes, Delete Files',
        variant: 'danger',
        requireTypedText: 'DELETE',
      }).subscribe(doubleConfirmed => {
        if (!doubleConfirmed) return;

        this.damebooru.resolveAllSameFolderDuplicates().subscribe({
          next: (result) => {
            this.reloadDuplicatesAndExcluded();
            this.toast.success(`Resolved ${result.resolvedGroups} group(s), deleted ${result.deletedPosts} post(s), skipped ${result.skippedGroups}.`);
          },
          error: () => this.toast.error('Failed to resolve same-folder duplicates.')
        });
      });
    });
  }

  resolveAllSameFolderExact() {
    const count = this.sameFolderGroups().filter(group => group.duplicateType === DuplicateType.Exact).length;
    if (count === 0) {
      return;
    }

    this.confirmService.confirm({
      title: 'Resolve Same-Folder Exact Duplicates',
      message: `Auto-resolve all ${count} same-folder exact groups? This keeps the highest-quality post and deletes the rest from disk.`,
      confirmText: 'Resolve All',
      variant: 'danger',
    }).subscribe(confirmed => {
      if (!confirmed) return;

      this.confirmService.confirm({
        title: 'Confirm File Deletion',
        message: 'This will DELETE files from disk. Do you REALLY want to delete all files in the selected exact duplicate groups?',
        confirmText: 'Yes, Delete Files',
        variant: 'danger',
        requireTypedText: 'DELETE',
      }).subscribe(doubleConfirmed => {
        if (!doubleConfirmed) return;

        this.damebooru.resolveAllSameFolderDuplicates(true).subscribe({
          next: (result) => {
            this.reloadDuplicatesAndExcluded();
            this.toast.success(`Resolved ${result.resolvedGroups} group(s), deleted ${result.deletedPosts} post(s), skipped ${result.skippedGroups}.`);
          },
          error: () => this.toast.error('Failed to resolve same-folder exact duplicates.')
        });
      });
    });
  }

  loadExcludedFiles() {
    this.damebooru.getExcludedFiles().subscribe({
      next: (files) => {
        this.excludedFiles.set(files);
      },
      error: () => {}
    });
  }

  onExcludedRowClick(file: ExcludedFile) {
    this.confirmService.confirm({
      title: 'Restore Excluded File',
      message: `Remove "${getFileNameFromPath(file.relativePath)}" from the exclusion list? It will be re-imported on the next scan.`,
      confirmText: 'Restore',
      variant: 'warning',
    }).subscribe(confirmed => {
      if (!confirmed) return;

      this.damebooru.unexcludeFile(file.id).subscribe({
        next: () => {
          this.excludedFiles.update(files => files.filter(current => current.id !== file.id));
          this.toast.success('File removed from exclusion list.');
        },
        error: () => this.toast.error('Failed to restore file.')
      });
    });
  }

  clearAllExcludedFiles() {
    const count = this.excludedFiles().length;
    if (count === 0) {
      return;
    }

    this.confirmService.confirm({
      title: 'Remove All Exclusions',
      message: `Remove all ${count} exclusions? Files will be re-imported on the next scan.`,
      confirmText: 'Remove All',
      variant: 'danger',
    }).subscribe(confirmed => {
      if (!confirmed) return;

      this.damebooru.clearExcludedFiles().subscribe({
        next: (result) => {
          this.excludedFiles.set([]);
          this.toast.success(`Removed ${result.removed} exclusion(s).`);
        },
        error: () => this.toast.error('Failed to remove exclusions.')
      });
    });
  }

  getExcludedFileContentUrl(file: ExcludedFile): string {
    return this.damebooru.getExcludedFileContentUrl(file.id);
  }

  getThumbnailUrl(post: Pick<VisibleDuplicatePost, 'thumbnailLibraryId' | 'thumbnailContentHash'>): string {
    return this.damebooru.getThumbnailUrl(post.thumbnailLibraryId, post.thumbnailContentHash);
  }

  onImageError(event: Event) {
    (event.target as HTMLImageElement).src = 'data:image/svg+xml,<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100"><rect fill="%23374151" width="100" height="100"/><text x="50" y="55" text-anchor="middle" fill="%239CA3AF" font-size="12">No image</text></svg>';
  }

  onExcludedImageError(event: Event) {
    (event.target as HTMLImageElement).src = 'data:image/svg+xml,<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100"><rect fill="%23374151" width="100" height="100"/><text x="50" y="55" text-anchor="middle" fill="%239CA3AF" font-size="12">No image</text></svg>';
  }

  onPostCardMouseEnter(postId: number) {
    if (!this.hoverPreviewEnabled()) {
      return;
    }

    this.hoveredPostId = postId;
    this.clearHoverPreviewTimer();
    this.hoverPreviewTimer = setTimeout(() => {
      this.hoverPreviewTimer = null;
      this.showPostPreview(postId);
    }, this.hoverPreviewDelayMs());
  }

  onPostCardMouseLeave(event: MouseEvent) {
    this.hoveredPostId = null;
    this.clearHoverPreviewTimer();

    const related = event.relatedTarget as Element | null;
    if (related?.closest('[data-preview-card]')) {
      return;
    }

    this.previewPost.set(null);
  }

  closePreview() {
    this.hoveredPostId = null;
    this.clearHoverPreviewTimer();
    this.previewPost.set(null);
  }

  @HostListener('document:keydown.escape')
  onEscapeKey() {
    this.closePreview();
  }

  private showPostPreview(postId: number) {
    if (this.hoveredPostId !== postId) {
      return;
    }

    const cached = this.previewPostCache.get(postId);
    if (cached) {
      this.previewPost.set(cached);
      return;
    }

    this.damebooru.getPost(postId).subscribe({
      next: (post) => {
        this.previewPostCache.set(postId, post);
        if (this.hoveredPostId === postId) {
          this.previewPost.set(post);
        }
      },
      error: () => {
        if (this.hoveredPostId === postId) {
          this.previewPost.set(null);
        }
      }
    });
  }

  private clearHoverPreviewTimer() {
    if (this.hoverPreviewTimer !== null) {
      clearTimeout(this.hoverPreviewTimer);
      this.hoverPreviewTimer = null;
    }
  }

  private selectBestQualityVisiblePostId(posts: ReadonlyArray<DuplicatePost>): number | null {
    if (posts.length === 0) {
      return null;
    }

    let best = posts[0];
    for (let i = 1; i < posts.length; i++) {
      const candidate = posts[i];

      const bestPixels = best.width * best.height;
      const candidatePixels = candidate.width * candidate.height;
      if (candidatePixels > bestPixels) {
        best = candidate;
        continue;
      }

      if (candidatePixels < bestPixels) {
        continue;
      }

      if (candidate.sizeBytes > best.sizeBytes) {
        best = candidate;
        continue;
      }

      if (candidate.sizeBytes < best.sizeBytes) {
        continue;
      }

      const candidateModified = Date.parse(candidate.fileModifiedDate);
      const bestModified = Date.parse(best.fileModifiedDate);
      if (candidateModified > bestModified) {
        best = candidate;
        continue;
      }

      if (candidateModified < bestModified) {
        continue;
      }

      if (candidate.id > best.id) {
        best = candidate;
      }
    }

    return best.id;
  }

  private reloadDuplicatesAndExcluded() {
    if (this.groupsTabInitialized) {
      this.loadGroups();
      this.loadSameFolderGroups();
    }

    if (this.resolvedTabInitialized) {
      this.loadResolvedGroups();
    }

    if (this.excludedTabInitialized) {
      this.loadExcludedFiles();
    }
  }
}
