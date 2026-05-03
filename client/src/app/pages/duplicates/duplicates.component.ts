import { ChangeDetectionStrategy, Component, DestroyRef, HostListener, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { finalize } from 'rxjs/operators';
import { filter, map, startWith } from 'rxjs';
import { DamebooruService } from '@services/api/damebooru/damebooru.service';
import {
  DuplicateType,
  DamebooruPostDto,
  DuplicateGroup,
  ExactDuplicateCluster,
  ExactDuplicateFile,
  DuplicateLookupMatch,
  DuplicateLookupResponse,
  DuplicatePostFile,
  DuplicatePost,
  ExcludedFile,
} from '@models';
import { FileNamePipe } from '@shared/pipes/file-name.pipe';
import { FileSizePipe } from '@shared/pipes/file-size.pipe';
import { formatPathForDisplay, getFileNameFromPath } from '@shared/utils/utils';
import { ConfirmService } from '@services/confirm.service';
import { ToastService } from '@services/toast.service';
import { TabsComponent } from '@shared/components/tabs/tabs.component';
import { TabComponent } from '@shared/components/tabs/tab.component';
import { ButtonDirective } from '@shared/directives';
import { DisplayPathPipe } from '@shared/pipes/display-path.pipe';
import { PaginatorComponent } from '@shared/components/paginator/paginator.component';
import { PostPreviewOverlayComponent } from '@shared/components/post-preview-overlay/post-preview-overlay.component';
import { PostPreviewHoverGateService } from '@shared/components/post-preview-overlay/post-preview-hover-gate.service';
import { SettingsService } from '@services/settings.service';
import { computeLookupContentHash } from './lookup-hash';
import { DuplicateCompareGroup, DuplicateCompareOverlayComponent, DuplicateComparePost } from './duplicate-compare-overlay.component';
import { DuplicateFolderBucketsComponent, DuplicateFolderBucketView, DuplicateFolderItemView } from './duplicate-folder-buckets.component';

type DuplicateViewFilter = 'all' | 'same-folder' | 'different-folder';

interface VisibleDuplicatePost {
  id: number;
  libraryId: number;
  libraryName: string;
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

interface VisibleDuplicateGroup {
  key: string;
  duplicateGroupId: number;
  type: DuplicateType;
  similarityPercent: number | null;
  detectedDate: string | null;
  posts: VisibleDuplicatePost[];
}

@Component({
  selector: 'app-duplicates-page',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, DisplayPathPipe, FileNamePipe, FileSizePipe, TabsComponent, TabComponent, ButtonDirective, PaginatorComponent, PostPreviewOverlayComponent, DuplicateCompareOverlayComponent, DuplicateFolderBucketsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './duplicates.component.html',
})
export class DuplicatesPageComponent {
  private readonly damebooru = inject(DamebooruService);
  private readonly confirmService = inject(ConfirmService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly settingsService = inject(SettingsService);
  private readonly previewHoverGate = inject(PostPreviewHoverGateService);
  private readonly currentUrl = toSignal(
    this.router.events.pipe(
      filter(event => event instanceof NavigationEnd),
      map(() => this.router.url),
      startWith(this.router.url),
    ),
    { initialValue: this.router.url },
  );
  readonly duplicateType = DuplicateType;
  readonly isLookupTabActive = computed(() => this.currentUrl().split('?')[0].endsWith('/duplicates/lookup'));
  readonly hoverPreviewEnabled = this.settingsService.enablePostPreviewOnHover;
  readonly hoverPreviewDelayMs = computed(() => {
    const raw = this.settingsService.postPreviewDelayMs();
    if (!Number.isFinite(raw)) {
      return 700;
    }

    return Math.max(0, Math.min(5000, Math.round(raw)));
  });

  groups = signal<DuplicateGroup[]>([]);
  exactClusters = signal<ExactDuplicateCluster[]>([]);
  exactCount = signal(0);
  perceptualCount = signal(0);

  resolvedGroups = signal<DuplicateGroup[]>([]);

  excludedFiles = signal<ExcludedFile[]>([]);

  lookupSelectedFile = signal<File | null>(null);
  lookupPreviewUrl = signal<string | null>(null);
  lookupResult = signal<DuplicateLookupResponse | null>(null);
  lookupError = signal<string | null>(null);
  lookupLoading = signal(false);
  lookupLoadingText = signal('Finding Matches...');
  lookupDropActive = signal(false);
  readonly lookupExactMatches = computed(() => this.lookupResult()?.exactMatches ?? []);
  readonly lookupPerceptualMatches = computed(() => this.lookupResult()?.perceptualMatches ?? []);
  readonly lookupPerceptualReason = computed(() => this.lookupResult()?.perceptualUnavailableReason ?? null);
  readonly lookupHasResults = computed(() => this.lookupResult() !== null);

  exactViewFilter = signal<DuplicateViewFilter>('all');
  previewPost = signal<DamebooruPostDto | null>(null);
  compareGroupKey = signal<string | null>(null);
  readonly visiblePageSize = 25;
  exactPage = signal(1);
  visiblePage = signal(1);
  readonly resolvedPageSize = 25;
  resolvedPage = signal(1);
  private exactTabInitialized = false;
  private perceptualTabInitialized = false;
  private groupsTabInitialized = false;
  private resolvedTabInitialized = false;
  private excludedTabInitialized = false;
  private hoverPreviewTimer: ReturnType<typeof setTimeout> | null = null;
  private hoveredPostId: number | null = null;
  private readonly previewPostCache = new Map<number, DamebooruPostDto>();

  constructor() {
    this.destroyRef.onDestroy(() => this.revokeLookupPreviewUrl());
  }

  onExactTabInit() {
    if (this.exactTabInitialized) {
      return;
    }

    this.exactTabInitialized = true;
    this.loadExactClusters();
  }

  onPerceptualTabInit() {
    if (this.perceptualTabInitialized) {
      return;
    }

    this.perceptualTabInitialized = true;
    this.loadGroups();
  }

  onGroupsTabInit() {
    if (this.groupsTabInitialized) {
      return;
    }

    this.groupsTabInitialized = true;
    this.loadExactClusters();
    this.loadGroups();
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

  onExactViewFilterChange(filter: DuplicateViewFilter) {
    this.exactViewFilter.set(filter);
    this.exactPage.set(1);
  }

  onLookupFileInputChange(event: Event) {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0] ?? null;
    this.setLookupFile(file);
    input.value = '';
  }

  onLookupDragOver(event: DragEvent) {
    event.preventDefault();
    this.lookupDropActive.set(true);
  }

  onLookupDragLeave(event: DragEvent) {
    event.preventDefault();
    this.lookupDropActive.set(false);
  }

  onLookupDrop(event: DragEvent) {
    event.preventDefault();
    this.lookupDropActive.set(false);
    const file = event.dataTransfer?.files?.[0] ?? null;
    this.setLookupFile(file);
  }

  runLookup() {
    const file = this.lookupSelectedFile();
    if (!file || this.lookupLoading()) {
      return;
    }

    this.lookupLoading.set(true);
    this.lookupError.set(null);

    if (this.isLookupImageFile(file)) {
      this.lookupLoadingText.set('Finding Matches...');
      this.damebooru.lookupDuplicates(file)
        .pipe(
          takeUntilDestroyed(this.destroyRef),
          finalize(() => {
            this.lookupLoading.set(false);
            this.lookupLoadingText.set('Finding Matches...');
          }),
        )
        .subscribe({
          next: result => {
            this.lookupResult.set(result);
          },
          error: () => {
            this.lookupResult.set(null);
            this.lookupError.set('Lookup failed. Please try a different file or check the server logs.');
            this.toast.error('Failed to look up similar posts.');
          }
        });
      return;
    }

    this.lookupLoadingText.set('Hashing File...');
    void this.runExactHashLookup(file);
  }

  clearLookup() {
    this.lookupSelectedFile.set(null);
    this.lookupResult.set(null);
    this.lookupError.set(null);
    this.lookupDropActive.set(false);
    this.lookupLoadingText.set('Finding Matches...');
    this.revokeLookupPreviewUrl();
  }

  hasLookupImagePreview(): boolean {
    return this.lookupPreviewUrl() !== null;
  }

  getLookupFileName(): string {
    return this.lookupSelectedFile()?.name ?? 'No file selected';
  }

  getLookupFileType(): string {
    return this.lookupSelectedFile()?.type || 'unknown';
  }

  getLookupFileSize(): number {
    return this.lookupSelectedFile()?.size ?? 0;
  }

  trackLookupMatch(_: number, match: DuplicateLookupMatch) {
    return `${match.id}:${match.similarityPercent ?? 'exact'}`;
  }

  readonly visibleGroups = computed<VisibleDuplicateGroup[]>(() => {
    return this.groups()
      .filter(group => group.type === DuplicateType.Perceptual)
      .map(group => {
      const recommendedKeepPostId = this.selectBestQualityVisiblePostId(group.posts);

      return {
        key: `${group.id}`,
        duplicateGroupId: group.id,
        type: group.type,
        similarityPercent: group.similarityPercent,
        detectedDate: group.detectedDate,
        posts: group.posts.map(post => ({
          id: post.id,
          libraryId: post.libraryId,
          libraryName: post.libraryName,
          relativePath: post.relativePath,
          width: post.width,
          height: post.height,
          sizeBytes: post.sizeBytes,
          fileModifiedDate: post.fileModifiedDate,
          thumbnailLibraryId: post.thumbnailLibraryId,
          thumbnailContentHash: post.thumbnailContentHash,
          isRecommendedKeep: recommendedKeepPostId !== null && post.id === recommendedKeepPostId,
          files: this.getDuplicatePostFiles(post),
          contentType: post.contentType,
        })),
      };
    });
  });

  readonly filteredExactClusters = computed(() => this.exactClusters().filter(cluster => {
    const filter = this.exactViewFilter();
    return filter === 'all'
      || (filter === 'same-folder' && cluster.hasSameFolderDuplicates)
      || (filter === 'different-folder' && cluster.hasCrossFolderDuplicates);
  }));

  readonly exactTotalPages = computed(() =>
    Math.max(1, Math.ceil(this.filteredExactClusters().length / this.visiblePageSize)));

  readonly exactCurrentPage = computed(() =>
    Math.min(this.exactPage(), this.exactTotalPages()));

  readonly pagedExactClusters = computed(() => {
    const start = (this.exactCurrentPage() - 1) * this.visiblePageSize;
    return this.filteredExactClusters().slice(start, start + this.visiblePageSize);
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

  readonly compareGroup = computed<DuplicateCompareGroup | null>(() => {
    const key = this.compareGroupKey();
    if (!key) {
      return null;
    }

    return this.visibleGroups().find(group => group.key === key) ?? null;
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

  getExactClusterCount(): number {
    return this.filteredExactClusters().length;
  }

  getExactFileCount(): number {
    return this.filteredExactClusters().reduce((sum, cluster) => sum + cluster.fileCount, 0);
  }

  getExactSameFolderCount(): number {
    return this.exactClusters().filter(cluster => cluster.hasSameFolderDuplicates).length;
  }

  getExactCrossFolderCount(): number {
    return this.exactClusters().filter(cluster => cluster.hasCrossFolderDuplicates).length;
  }

  getExactFolderBucketViews(cluster: ExactDuplicateCluster): DuplicateFolderBucketView[] {
    return cluster.folders.map(folder => ({
      key: `${folder.libraryId}:${folder.folderPath}`,
      libraryName: folder.libraryName,
      folderPath: folder.folderPath,
      items: folder.files.map(file => ({
        key: `exact:${file.postFileId}`,
        kind: 'exact-file' as const,
        postId: file.postId,
        actionTargetId: file.postFileId,
        relativePath: file.relativePath,
        width: file.width,
        height: file.height,
        sizeBytes: file.sizeBytes,
        thumbnailUrl: this.getExactFileThumbnailUrl(file),
        canDelete: true,
        isRecommendedKeep: false,
      })),
    }));
  }

  openCompareGroup(group: VisibleDuplicateGroup): void {
    this.closePreview();
    this.compareGroupKey.set(group.key);
  }

  closeCompareGroup(): void {
    this.compareGroupKey.set(null);
  }

  onCompareExcludePost(group: DuplicateCompareGroup, post: DuplicateComparePost): void {
    this.onExcludePost(group, post);
  }

  onCompareDeletePost(group: DuplicateCompareGroup, post: DuplicateComparePost): void {
    this.onDeletePost(group, post);
  }

  onExcludeExactFolderItem(item: DuplicateFolderItemView): void {
    this.onExcludeExactFile({
      postId: item.postId,
      postFileId: item.actionTargetId,
      libraryId: 0,
      libraryName: '',
      relativePath: item.relativePath,
      contentHash: '',
      width: item.width,
      height: item.height,
      contentType: '',
      sizeBytes: item.sizeBytes,
      fileModifiedDate: '',
      thumbnailLibraryId: 0,
      thumbnailContentHash: '',
    });
  }

  onDeleteExactFolderItem(item: DuplicateFolderItemView): void {
    this.onDeleteExactFile({
      postId: item.postId,
      postFileId: item.actionTargetId,
      libraryId: 0,
      libraryName: '',
      relativePath: item.relativePath,
      contentHash: '',
      width: item.width,
      height: item.height,
      contentType: '',
      sizeBytes: item.sizeBytes,
      fileModifiedDate: '',
      thumbnailLibraryId: 0,
      thumbnailContentHash: '',
    });
  }

  onAutoResolveGroup(group: DuplicateCompareGroup) {
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

  onDismissGroup(group: DuplicateCompareGroup) {
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

  onExcludePost(group: DuplicateCompareGroup, post: DuplicateComparePost) {
    const fileCount = post.files.length;
    const placementList = post.files
      .map(file => `- ${this.getPostFilePlacementLabel(file)}`)
      .join('\n');
    const message = [
      `Exclude "${getFileNameFromPath(post.relativePath)}" from the booru?`,
      '',
      `The physical ${fileCount === 1 ? 'file will remain' : 'files will remain'} on disk, but ${fileCount === 1 ? 'it' : 'they'} will not be imported again:`,
      placementList,
    ].join('\n');

    this.confirmService.confirm({
      title: 'Exclude Post',
      message,
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

  onDeletePost(group: DuplicateCompareGroup, post: DuplicateComparePost) {
    const fileCount = post.files.length;
    const placementList = post.files
      .map(file => `- ${this.getPostFilePlacementLabel(file)}`)
      .join('\n');
    const message = [
      `Delete "${getFileNameFromPath(post.relativePath)}" from disk and remove it from the booru?`,
      '',
      `This will permanently delete ${fileCount} file${fileCount === 1 ? '' : 's'} attached to this post:`,
      placementList,
    ].join('\n');

    this.confirmService.confirm({
      title: 'Delete Post and Files',
      message,
      confirmText: 'Delete',
      variant: 'danger',
    }).subscribe(confirmed => {
      if (!confirmed) return;

      this.damebooru.deletePostFromGroup(group.duplicateGroupId, post.id).subscribe({
        next: () => {
          this.reloadDuplicatesAndExcluded();
          this.toast.success('Duplicate post and files deleted.');
        },
        error: () => this.toast.error('Failed to delete duplicate post.')
      });
    });
  }

  onExcludeExactFile(file: ExactDuplicateFile) {
    this.confirmService.confirm({
      title: 'Exclude File',
      message: `Exclude "${getFileNameFromPath(file.relativePath)}" from the board? The file will remain on disk and will not be imported again.`,
      confirmText: 'Exclude',
      variant: 'warning',
    }).subscribe(confirmed => {
      if (!confirmed) return;

      this.damebooru.excludeExactDuplicateFile(file.postFileId).subscribe({
        next: () => {
          this.reloadDuplicatesAndExcluded();
          this.toast.success('Exact duplicate file excluded.');
        },
        error: () => this.toast.error('Failed to exclude exact duplicate file.')
      });
    });
  }

  onDeleteExactFile(file: ExactDuplicateFile) {
    this.confirmService.confirm({
      title: 'Delete File',
      message: `Delete "${getFileNameFromPath(file.relativePath)}" from disk and remove it from the board?`,
      confirmText: 'Delete',
      variant: 'danger',
    }).subscribe(confirmed => {
      if (!confirmed) return;

      this.damebooru.deleteExactDuplicateFile(file.postFileId).subscribe({
        next: () => {
          this.reloadDuplicatesAndExcluded();
          this.toast.success('Exact duplicate file deleted.');
        },
        error: () => this.toast.error('Failed to delete exact duplicate file.')
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
      error: () => void 0
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

  onExactPageChange(page: number) {
    this.exactPage.set(page);
  }

  onResolvedPageChange(page: number) {
    this.resolvedPage.set(page);
  }

  loadGroups() {
    this.damebooru.getDuplicateGroups().subscribe({
      next: (groups) => {
        this.groups.set(groups);
        this.visiblePage.set(Math.min(this.visiblePage(), this.visibleTotalPages()));
        this.perceptualCount.set(groups.filter(group => group.type === DuplicateType.Perceptual).length);
      },
      error: () => void 0
    });
  }

  loadExactClusters() {
    this.damebooru.getExactDuplicateClusters().subscribe({
      next: (clusters) => {
        this.exactClusters.set(clusters);
        this.exactCount.set(clusters.length);
        this.exactPage.set(Math.min(this.exactPage(), this.exactTotalPages()));
      },
      error: () => void 0
    });
  }

  loadExcludedFiles() {
    this.damebooru.getExcludedFiles().subscribe({
      next: (files) => {
        this.excludedFiles.set(files);
      },
      error: () => void 0
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

  getExactFileThumbnailUrl(file: Pick<ExactDuplicateFile, 'thumbnailLibraryId' | 'thumbnailContentHash'>): string {
    return this.damebooru.getThumbnailUrl(file.thumbnailLibraryId, file.thumbnailContentHash);
  }

  trackExactCluster(_: number, cluster: ExactDuplicateCluster) {
    return cluster.contentHash;
  }

  trackExactFolder(_: number, folder: ExactDuplicateCluster['folders'][number]) {
    return `${folder.libraryId}:${folder.folderPath}`;
  }

  trackExactFile(_: number, file: ExactDuplicateFile) {
    return file.postFileId;
  }

  trackPostFile(_: number, file: DuplicatePostFile): string {
    return file.postFileId > 0 ? `${file.postFileId}` : `${file.libraryId}:${file.relativePath}`;
  }

  getDisplayedPostFiles(post: VisibleDuplicatePost): DuplicatePostFile[] {
    return post.files.slice(0, 3);
  }

  getHiddenPostFileCount(post: VisibleDuplicatePost): number {
    return Math.max(0, post.files.length - 3);
  }

  getPostFilePlacementLabel(file: DuplicatePostFile): string {
    const libraryName = file.libraryName || `Library #${file.libraryId}`;
    return `${libraryName} / ${formatPathForDisplay(file.relativePath)}`;
  }

  getLookupThumbnailUrl(match: DuplicateLookupMatch): string {
    return this.damebooru.getThumbnailUrl(match.thumbnailLibraryId, match.thumbnailContentHash);
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

    if (this.previewHoverGate.isSuppressed()) {
      return;
    }

    this.hoveredPostId = postId;
    this.clearHoverPreviewTimer();
    this.hoverPreviewTimer = setTimeout(() => {
      this.hoverPreviewTimer = null;
      this.showPostPreview(postId);
    }, this.hoverPreviewDelayMs());
  }

  onPostCardMouseMove(postId: number) {
    if (!this.hoverPreviewEnabled() || !this.previewHoverGate.resumeIfSuppressed()) {
      return;
    }

    this.onPostCardMouseEnter(postId);
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
    this.previewHoverGate.suppressUntilPointerMove();
    this.previewPost.set(null);
  }

  @HostListener('document:keydown.escape')
  onEscapeKey() {
    this.closePreview();
  }

  @HostListener('document:paste', ['$event'])
  onPaste(event: ClipboardEvent) {
    if (!this.isLookupTabActive()) {
      return;
    }

    const items = event.clipboardData?.items;
    if (!items || items.length === 0) {
      return;
    }

    for (const item of Array.from(items)) {
      if (!item.type.startsWith('image/')) {
        continue;
      }

      const blob = item.getAsFile();
      if (!blob) {
        continue;
      }

      event.preventDefault();
      const extension = this.getLookupFileExtension(blob.type);
      const file = new File([blob], `clipboard-image${extension}`, { type: blob.type });
      this.setLookupFile(file);
      return;
    }
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

  private selectBestQualityVisiblePostId(posts: readonly DuplicatePost[]): number | null {
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

  private getDuplicatePostFiles(post: DuplicatePost): DuplicatePostFile[] {
    if (post.files?.length > 0) {
      return post.files;
    }

    return [{
      postFileId: 0,
      libraryId: post.libraryId,
      libraryName: post.libraryName,
      relativePath: post.relativePath,
    }];
  }

  private reloadDuplicatesAndExcluded() {
    if (this.exactTabInitialized || this.groupsTabInitialized) {
      this.loadExactClusters();
    }

    if (this.groupsTabInitialized) {
      this.loadGroups();
    }

    if (this.perceptualTabInitialized) {
      this.loadGroups();
    }

    if (this.resolvedTabInitialized) {
      this.loadResolvedGroups();
    }

    if (this.excludedTabInitialized) {
      this.loadExcludedFiles();
    }
  }

  private setLookupFile(file: File | null) {
    this.lookupResult.set(null);
    this.lookupError.set(null);
    this.lookupSelectedFile.set(file);
    this.revokeLookupPreviewUrl();

    if (file && this.isLookupImageFile(file)) {
      this.lookupPreviewUrl.set(URL.createObjectURL(file));
    }
  }

  private revokeLookupPreviewUrl() {
    const currentUrl = this.lookupPreviewUrl();
    if (currentUrl) {
      URL.revokeObjectURL(currentUrl);
      this.lookupPreviewUrl.set(null);
    }
  }

  private isLookupImageFile(file: File): boolean {
    return file.type.startsWith('image/')
      || /\.(jpg|jpeg|png|gif|bmp|tga|webp|jxl)$/i.test(file.name);
  }

  private getLookupFileExtension(contentType: string): string {
    switch (contentType.toLowerCase()) {
      case 'image/jpeg':
        return '.jpg';
      case 'image/png':
        return '.png';
      case 'image/gif':
        return '.gif';
      case 'image/webp':
        return '.webp';
      case 'image/bmp':
        return '.bmp';
      case 'image/tga':
        return '.tga';
      case 'image/jxl':
        return '.jxl';
      default:
        return '';
    }
  }

  private async runExactHashLookup(file: File): Promise<void> {
    try {
      const contentHash = await computeLookupContentHash(file);
      this.lookupLoadingText.set('Finding Exact Matches...');

      this.damebooru.lookupDuplicatesByHash({
        fileName: file.name,
        contentType: file.type || 'application/octet-stream',
        sizeBytes: file.size,
        contentHash,
      })
        .pipe(
          takeUntilDestroyed(this.destroyRef),
          finalize(() => {
            this.lookupLoading.set(false);
            this.lookupLoadingText.set('Finding Matches...');
          }),
        )
        .subscribe({
          next: result => {
            this.lookupResult.set(result);
          },
          error: () => {
            this.lookupResult.set(null);
            this.lookupError.set('Lookup failed. Please try a different file or check the server logs.');
            this.toast.error('Failed to look up similar posts.');
          }
        });
    } catch {
      this.lookupLoading.set(false);
      this.lookupLoadingText.set('Finding Matches...');
      this.lookupResult.set(null);
      this.lookupError.set('Could not hash this file in the browser.');
      this.toast.error('Failed to hash file locally.');
    }
  }
}
