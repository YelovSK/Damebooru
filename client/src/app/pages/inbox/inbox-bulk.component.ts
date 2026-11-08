import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CdkMenuTrigger } from '@angular/cdk/menu';
import { RouterLink } from '@angular/router';

import { AppLinks } from '@app/app.paths';
import { type DamebooruPostDto, type FolderLocation, type FolderSuggestion } from '@models';
import { DamebooruService } from '@services/api/damebooru/damebooru.service';
import { ButtonDirective, MenuDirective, MenuItemDirective } from '@shared/directives';
import { PostHoverPreview } from '@shared/components/post-preview-overlay/post-hover-preview';
import { PostPreviewOverlayComponent } from '@shared/components/post-preview-overlay/post-preview-overlay.component';
import { getFileNameFromPath } from '@shared/utils/utils';

import { DestinationPickerComponent, type PartialDestination } from './destination-picker.component';
import { InboxMediaComponent } from './inbox-media.component';
import { ALREADY_IN_LIBRARY_GROUP, InboxStore, type PredictionState, folderKey, groupLocations } from './inbox.store';

interface Group {
  key: string;
  label: string;
  posts: DamebooruPostDto[];
}

/**
 * Posts grouped by where they are suggested to go, each with its own editable destinations.
 * The side panel edits the selection: the active post, or several posts picked with Ctrl/Shift or a group's title.
 */
@Component({
  selector: 'app-inbox-bulk',
  standalone: true,
  imports: [DecimalPipe, RouterLink, CdkMenuTrigger, ButtonDirective, MenuDirective, MenuItemDirective, PostPreviewOverlayComponent, DestinationPickerComponent, InboxMediaComponent],
  providers: [PostHoverPreview],
  host: {
    class: 'block flex-1 min-h-0 overflow-y-auto',
    '(document:keydown)': 'onKeydown($event)',
  },
  templateUrl: './inbox-bulk.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class InboxBulkComponent {
  readonly store = inject(InboxStore);
  readonly hoverPreview = inject(PostHoverPreview);
  readonly appLinks = AppLinks;
  private readonly damebooru = inject(DamebooruService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly alreadyInLibraryGroup = ALREADY_IN_LIBRARY_GROUP;
  /** The post the preview shows and the arrow keys move from. */
  readonly activePostId = this.store.selectedPostId;
  /** Posts selected along with the active one; empty while only the active post is selected. */
  readonly selectedIds = signal<ReadonlySet<number>>(new Set());
  readonly showHelp = signal(false);

  private readonly picker = viewChild(DestinationPickerComponent);
  private lastActiveIndex = 0;
  /** Where Shift selections start from. */
  private anchorId: number | null = null;

  readonly view = computed(() => {
    const groupOf = this.store.groups();
    const byKey = new Map<string, DamebooruPostDto[]>();
    let pending = 0;
    for (const post of this.store.posts()) {
      const key = groupOf.get(post.id);
      if (key === undefined) {
        pending++;
        continue;
      }

      let posts = byKey.get(key);
      if (!posts) byKey.set(key, posts = []);
      posts.push(post);
    }

    const groups: Group[] = [...byKey]
      .map(([key, posts]) => ({
        key,
        label: key === ALREADY_IN_LIBRARY_GROUP ? 'Already in your library'
          : key === '' ? 'Unsure'
          : groupLocations(key).map(l => this.store.label(l)).join(' + '),
        posts,
      }))
      // Duplicates come first, as they are the quickest to clear; unsure posts go last, as they need a decision per post.
      .sort((a, b) => Number(b.key === ALREADY_IN_LIBRARY_GROUP) - Number(a.key === ALREADY_IN_LIBRARY_GROUP)
        || Number(a.key === '') - Number(b.key === '')
        || b.posts.length - a.posts.length
        || a.label.localeCompare(b.label));
    return { groups, ordered: groups.flatMap(g => g.posts), pending };
  });

  readonly activeRows = computed(() => {
    const ordered = this.view().ordered;
    const selected = this.selectedIds();
    const rows = ordered.filter(p => selected.has(p.id));
    if (rows.length > 0) return rows;

    const post = ordered.find(p => p.id === this.activePostId());
    return post ? [post] : [];
  });
  readonly activeIds = computed(() => new Set(this.activeRows().map(p => p.id)));
  readonly selectedGroups = computed(() => {
    if (this.selectedIds().size === 0) return new Set<string>();
    const ids = this.activeIds();
    return new Set(this.view().groups.filter(g => g.posts.every(p => ids.has(p.id))).map(g => g.key));
  });
  /** Every destination in the selection, with how many of the selected posts have it. */
  private readonly selectionDestinations = computed(() => {
    const byKey = new Map<string, PartialDestination>();
    for (const post of this.activeRows()) {
      for (const location of this.store.draftOf(post.id)) {
        const entry = byKey.get(folderKey(location));
        if (entry) entry.count++;
        else byKey.set(folderKey(location), { location, count: 1 });
      }
    }
    return [...byKey.values()];
  });
  readonly commonDestinations = computed(() => {
    const size = this.activeRows().length;
    return this.selectionDestinations().filter(d => d.count === size).map(d => d.location);
  });
  readonly partialDestinations = computed(() => {
    const size = this.activeRows().length;
    return this.selectionDestinations().filter(d => d.count < size);
  });
  readonly canAccept = computed(() => {
    return !this.store.busy() && this.activeRows().some(p => this.store.draftOf(p.id).length > 0);
  });
  readonly preview = computed(() => this.activeRows().find(p => p.id === this.activePostId()) ?? this.activeRows()[0] ?? null);
  /** In list order, so tagging works down the list as shown. */
  readonly imagePosts = computed(() => this.view().ordered.filter(p => p.contentType.startsWith('image/')));

  constructor() {
    effect(() => this.store.requestSuggestions(this.store.posts().map(p => p.id)));

    // When the selected post leaves the list (sorted or deleted), the post that took its place is selected.
    effect(() => {
      const ordered = this.view().ordered;
      untracked(() => {
        const index = ordered.findIndex(p => p.id === this.activePostId());
        if (index >= 0) {
          this.lastActiveIndex = index;
          return;
        }

        const remaining = ordered.find(p => this.selectedIds().has(p.id));
        if (remaining) {
          this.activePostId.set(remaining.id);
          return;
        }

        this.selectedIds.set(new Set());
        this.activePostId.set(ordered[Math.min(this.lastActiveIndex, ordered.length - 1)]?.id ?? null);
      });
    });
  }

  thumbnailUrl(post: DamebooruPostDto): string {
    return this.damebooru.getThumbnailUrl(post.contentHash);
  }

  fileName(post: DamebooruPostDto): string {
    return getFileNameFromPath(post.relativePath);
  }

  draftOf(post: DamebooruPostDto): FolderLocation[] {
    return this.store.draftOf(post.id);
  }

  /** Suggestions that aren't destinations yet, offered as one-click additions. */
  extraSuggestions(post: DamebooruPostDto): FolderSuggestion[] {
    const chosen = new Set(this.draftOf(post).map(folderKey));
    return (this.store.suggestions().get(post.id) ?? []).filter(s => !chosen.has(folderKey(s)));
  }

  scoreOf(post: DamebooruPostDto, location: FolderLocation): number | null {
    return this.store.suggestions().get(post.id)?.find(s => folderKey(s) === folderKey(location))?.score ?? null;
  }

  predictionOf(post: DamebooruPostDto): PredictionState | null {
    return this.store.predictions().get(post.id) ?? null;
  }

  /** Images the AI hasn't seen yet; while queued or running, the overlay shows that instead. */
  lacksPrediction(post: DamebooruPostDto): boolean {
    return this.store.needsPrediction().has(post.id) && post.contentType.startsWith('image/') && !this.predictionOf(post);
  }

  predictNew(): void {
    const needed = this.store.needsPrediction();
    this.store.predict(this.imagePosts().filter(p => needed.has(p.id)).map(p => p.id));
  }

  /** For when the predictions are stale, e.g. after switching the tagger's model. */
  retagAll(): void {
    this.store.predict(this.imagePosts().map(p => p.id));
  }

  onRowClick(post: DamebooruPostDto, event: MouseEvent): void {
    if (event.shiftKey) {
      this.selectRange(post);
    } else if (event.ctrlKey || event.metaKey) {
      this.toggleSelected(post);
    } else {
      this.activate(post);
    }
  }

  activate(post: DamebooruPostDto): void {
    this.selectedIds.set(new Set());
    this.anchorId = post.id;
    this.focusPost(post);
  }

  activateGroup(group: Group): void {
    this.selectedIds.set(new Set(group.posts.map(p => p.id)));
    this.anchorId = group.posts[0]?.id ?? null;
    this.activePostId.set(this.anchorId);
  }

  private toggleSelected(post: DamebooruPostDto): void {
    const next = new Set(this.activeIds());
    this.anchorId = post.id;
    if (!next.has(post.id)) {
      next.add(post.id);
      this.selectedIds.set(next);
      this.focusPost(post);
      return;
    }

    if (next.size === 1) return;
    next.delete(post.id);
    this.selectedIds.set(next);
    if (post.id === this.activePostId()) {
      this.activePostId.set(this.activeRows()[0]?.id ?? null);
    }
  }

  private selectRange(post: DamebooruPostDto): void {
    const ordered = this.view().ordered;
    const from = ordered.findIndex(p => p.id === (this.anchorId ?? this.activePostId()));
    const to = ordered.findIndex(p => p.id === post.id);
    if (from < 0 || to < 0) {
      this.activate(post);
      return;
    }

    this.selectedIds.set(new Set(ordered.slice(Math.min(from, to), Math.max(from, to) + 1).map(p => p.id)));
    this.focusPost(post);
  }

  focusPost(post: DamebooruPostDto): void {
    this.activePostId.set(post.id);
    queueMicrotask(() => this.host.nativeElement.querySelector(`[data-post-id="${post.id}"]`)?.scrollIntoView({ block: 'nearest' }));
  }

  addToRow(post: DamebooruPostDto, location: FolderLocation): void {
    this.store.addDestination([post.id], location);
  }

  removeFromRow(post: DamebooruPostDto, location: FolderLocation): void {
    this.store.removeDestination([post.id], location);
  }

  /** Picker edits apply only the change to each selected post, so destinations that differ between posts survive. */
  addToSelection(location: FolderLocation): void {
    this.store.addDestination(this.activeRows().map(p => p.id), location);
  }

  removeFromSelection(location: FolderLocation): void {
    this.store.removeDestination(this.activeRows().map(p => p.id), location);
  }

  clearSelection(): void {
    this.store.clearDestinations(this.activeRows().map(p => p.id));
  }

  accept(posts: DamebooruPostDto[]): void {
    const items = posts
      .map(p => ({ postId: p.id, destinations: this.draftOf(p) }))
      .filter(i => i.destinations.length > 0);
    if (items.length === 0 || this.store.busy()) return;

    this.store.sort(items).pipe(takeUntilDestroyed(this.destroyRef)).subscribe();
  }

  acceptSelection(): void {
    this.accept(this.activeRows());
  }

  acceptGroup(group: Group): void {
    this.accept(group.posts);
  }

  discardGroup(group: Group): void {
    this.store.confirmDiscard(group.posts).pipe(takeUntilDestroyed(this.destroyRef)).subscribe();
  }

  existingOf(post: DamebooruPostDto): FolderLocation[] {
    return this.store.existingLocations().get(post.id) ?? [];
  }

  discardSelection(): void {
    this.store.confirmDiscard(this.activeRows()).pipe(takeUntilDestroyed(this.destroyRef)).subscribe();
  }

  onKeydown(event: KeyboardEvent): void {
    if (event.defaultPrevented || this.store.busy()) return;

    if (event.ctrlKey && event.key === 'Enter') {
      event.preventDefault();
      const key = this.store.groups().get(this.activePostId() ?? -1);
      const group = this.view().groups.find(g => g.key === key);
      if (group) this.acceptGroup(group);
      return;
    }

    if (event.key === 'Escape') {
      this.hoverPreview.close();
      const focused = document.activeElement as HTMLElement | null;
      focused?.blur();
      if (focused instanceof HTMLInputElement) return;

      this.selectedIds.set(new Set());
      this.anchorId = this.activePostId();
      return;
    }

    const target = event.target;
    const typing = target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement;
    const pressingButton = target instanceof HTMLButtonElement && (event.key === 'Enter' || event.key === ' ');
    if (typing || pressingButton) return;

    switch (event.key) {
      case 'ArrowDown':
      case 'ArrowUp':
        event.preventDefault();
        this.move(event.key === 'ArrowDown' ? 1 : -1, event.shiftKey);
        return;
      case 'Enter':
        event.preventDefault();
        this.acceptSelection();
        return;
      case 'Delete':
        event.preventDefault();
        this.discardSelection();
        return;
      case 'Backspace':
        event.preventDefault();
        this.clearSelection();
        return;
    }

    if (event.key.length === 1 && !event.ctrlKey && !event.altKey && !event.metaKey) {
      this.picker()?.focusNow();
    }
  }

  private move(offset: number, extend: boolean): void {
    const ordered = this.view().ordered;
    const index = ordered.findIndex(p => p.id === this.activePostId());
    const next = ordered[Math.max(0, Math.min(index + offset, ordered.length - 1))];
    if (!next) return;

    if (extend) {
      this.selectRange(next);
    } else {
      this.activate(next);
    }
  }
}
