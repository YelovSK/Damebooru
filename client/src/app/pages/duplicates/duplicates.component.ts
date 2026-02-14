import { ChangeDetectionStrategy, Component, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { DuplicateService, DuplicateGroup, DuplicatePost } from '../../services/api/duplicate.service';
import { environment } from '../../../environments/environment';
import { FileNamePipe } from '@shared/pipes/file-name.pipe';
import { FileSizePipe } from '@shared/pipes/file-size.pipe';
import { getFileNameFromPath } from '@shared/utils/utils';
import { ConfirmService } from '@services/confirm.service';
import { ToastService } from '@services/toast.service';

@Component({
  selector: 'app-duplicates-page',
  standalone: true,
  imports: [CommonModule, RouterLink, FileNamePipe, FileSizePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div>
      <h1 class="text-3xl font-bold mb-2 text-accent-primary">Duplicate Detection</h1>
      <p class="text-gray-400 mb-6">Review and resolve duplicate posts. Run "Find Duplicates" from the Jobs page to detect new groups.</p>

      <!-- Summary Bar -->
      <div class="mb-6 flex flex-wrap gap-3">
        <div class="flex-1 min-w-[12rem] rounded-lg border border-gray-700 bg-gray-900 px-6 py-4">
          <div class="text-3xl font-bold text-white">{{ groups().length }}</div>
          <div class="text-sm text-gray-400">Unresolved Groups</div>
        </div>
        <div class="flex-1 min-w-[12rem] rounded-lg border border-gray-700 bg-gray-900 px-6 py-4">
          <div class="text-3xl font-bold text-blue-400">{{ exactCount() }}</div>
          <div class="text-sm text-gray-400">Exact (Content Hash)</div>
        </div>
        <div class="flex-1 min-w-[12rem] rounded-lg border border-gray-700 bg-gray-900 px-6 py-4">
          <div class="text-3xl font-bold text-purple-400">{{ perceptualCount() }}</div>
          <div class="text-sm text-gray-400">Perceptual (dHash)</div>
        </div>
      </div>

      <!-- Bulk Actions -->
      <div *ngIf="exactCount() > 0" class="mb-6">
        <button (click)="resolveAllExact()"
                class="bg-blue-600 hover:bg-blue-700 text-white font-bold py-2 px-6 rounded transition-colors">
          Deduplicate All Exact ({{ exactCount() }} groups)
        </button>
        <span class="text-xs text-gray-500 ml-3">Keeps the oldest post in each group</span>
      </div>

      <!-- Empty State -->
      <div *ngIf="!loading() && groups().length === 0" class="text-center py-16">
        <div class="text-6xl mb-4">✓</div>
        <h2 class="text-xl font-semibold text-white mb-2">No duplicates found</h2>
        <p class="text-gray-400">Run "Find Duplicates" from the Jobs page to scan for duplicates.</p>
      </div>

      <!-- Loading -->
      <div *ngIf="loading()" class="text-center py-16 text-gray-400">Loading...</div>

      <!-- Duplicate Groups -->
      <div *ngFor="let group of groups(); trackBy: trackGroup" class="bg-gray-900 border border-gray-700 rounded-lg p-4 md:p-5 mb-6 shadow-lg">
        <!-- Group Header -->
        <div class="mb-4 flex flex-wrap items-center justify-between gap-3">
          <div class="flex items-center gap-3">
            <span [class]="group.type === 'exact'
              ? 'bg-blue-600 text-white px-2 py-1 rounded text-xs font-bold'
              : 'bg-purple-600 text-white px-2 py-1 rounded text-xs font-bold'">
              {{ group.type === 'exact' ? 'EXACT' : 'PERCEPTUAL' }}
            </span>
            <span *ngIf="group.similarityPercent" class="text-sm text-gray-400">
              ~{{ group.similarityPercent }}% similar
            </span>
            <span class="text-sm text-gray-500">
              {{ group.posts.length }} posts · detected {{ group.detectedDate | date:'short' }}
            </span>
          </div>
          <button (click)="keepAll(group)"
                  class="bg-gray-700 hover:bg-gray-600 text-white px-4 py-2 rounded text-sm transition-colors">
            Keep All
          </button>
        </div>

        <!-- Thumbnails Grid -->
        <div class="flex flex-wrap gap-3">
          <div *ngFor="let post of group.posts; trackBy: trackPost"
               class="relative w-36 sm:w-40 md:w-44 group/card bg-gray-800 rounded-lg overflow-hidden border border-gray-700 hover:border-accent-primary transition-colors"
          >
            <!-- Thumbnail -->
            <div
              class="relative aspect-square overflow-hidden cursor-pointer"
              role="button"
              tabindex="0"
              (click)="keepOne(group, post)"
              (keydown.enter)="keepOne(group, post)"
              (keydown.space)="keepOne(group, post); $event.preventDefault()">
              <img [src]="mediaBase + post.thumbnailUrl" [alt]="post.relativePath"
                   class="w-full h-full object-cover" loading="lazy"
                   (error)="onImageError($event)">
              <!-- Keep this overlay on hover -->
              <div class="absolute inset-0 bg-black/60 opacity-0 group-hover/card:opacity-100 transition-opacity flex items-center justify-center">
                <span class="bg-accent-primary text-white font-bold px-4 py-2 rounded text-sm">Keep This</span>
              </div>
            </div>

            <!-- Info overlay -->
            <a [routerLink]="['/post', post.id]" class="block p-2 hover:bg-gray-700/50 transition-colors">
              <div class="text-xs text-gray-400 truncate mb-1" [title]="post.relativePath">{{ post.relativePath | fileName }}</div>
              <div class="flex justify-between text-xs text-gray-500">
                <span>{{ post.width }}×{{ post.height }}</span>
                <span>{{ post.sizeBytes | fileSize }}</span>
              </div>
            </a>
          </div>
        </div>
      </div>
    </div>
  `
})
export class DuplicatesPageComponent implements OnInit {
  groups = signal<DuplicateGroup[]>([]);
  loading = signal(true);

  exactCount = signal(0);
  perceptualCount = signal(0);

  mediaBase = environment.mediaBaseUrl;

  constructor(
    private duplicateService: DuplicateService,
    private confirmService: ConfirmService,
    private toast: ToastService,
  ) { }

  ngOnInit() {
    this.loadGroups();
  }

  loadGroups() {
    this.loading.set(true);
    this.duplicateService.getGroups().subscribe({
      next: (groups) => {
        this.groups.set(groups);
        this.recountTypes();
        this.loading.set(false);
      },
      error: () => this.loading.set(false)
    });
  }

  keepAll(group: DuplicateGroup) {
    this.duplicateService.keepAll(group.id).subscribe(() => {
      this.groups.update(groups => groups.filter(g => g.id !== group.id));
      this.recountTypes();
    });
  }

  keepOne(group: DuplicateGroup, post: DuplicatePost) {
    this.confirmService.confirm({
      title: 'Keep One Post',
      message: `Keep "${getFileNameFromPath(post.relativePath)}" and remove the other ${group.posts.length - 1} post(s) from the booru?`,
      confirmText: 'Keep This',
      variant: 'danger',
    }).subscribe(confirmed => {
      if (!confirmed) return;

      this.duplicateService.keepOne(group.id, post.id).subscribe(() => {
        this.groups.update(groups => groups.filter(g => g.id !== group.id));
        this.recountTypes();
      });
    });
  }

  resolveAllExact() {
    const count = this.exactCount();

    this.confirmService.confirm({
      title: 'Resolve Exact Duplicates',
      message: `Resolve all ${count} exact duplicate groups? This keeps the oldest post and removes the others from the booru.`,
      confirmText: 'Resolve All',
      variant: 'danger',
    }).subscribe(confirmed => {
      if (!confirmed) return;

      this.duplicateService.resolveAllExact().subscribe({
        next: (result) => {
          this.loadGroups();
          this.toast.success(`Resolved ${result.resolved} exact duplicate groups.`);
        },
        error: (err) => this.toast.error('Failed: ' + (err?.message || 'Unknown error'))
      });
    });
  }

  onImageError(event: Event) {
    (event.target as HTMLImageElement).src = 'data:image/svg+xml,<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100"><rect fill="%23374151" width="100" height="100"/><text x="50" y="55" text-anchor="middle" fill="%239CA3AF" font-size="12">No image</text></svg>';
  }

  trackGroup(_: number, group: DuplicateGroup) { return group.id; }
  trackPost(_: number, post: DuplicatePost) { return post.id; }

  private recountTypes() {
    const groups = this.groups();
    this.exactCount.set(groups.filter(g => g.type === 'exact').length);
    this.perceptualCount.set(groups.filter(g => g.type === 'perceptual').length);
  }
}
