import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { type DamebooruPostDto, type FolderLocation, type FolderSuggestion } from '@models';
import { DamebooruService } from '@services/api/damebooru/damebooru.service';
import { ButtonDirective } from '@shared/directives';
import { PostHoverPreview } from '@shared/components/post-preview-overlay/post-hover-preview';
import { PostPreviewOverlayComponent } from '@shared/components/post-preview-overlay/post-preview-overlay.component';
import { getFileNameFromPath } from '@shared/utils/utils';

import { DestinationPickerComponent } from './destination-picker.component';
import { InboxMediaComponent } from './inbox-media.component';
import { InboxStore, type PredictionState, folderKey, groupLocations } from './inbox.store';

interface Group {
  key: string;
  label: string;
  posts: DamebooruPostDto[];
}

/**
 * Posts grouped by where they are suggested to go, each with its own editable destinations.
 * A selection is one post, or a whole group after clicking its title; the side panel edits the selection.
 */
@Component({
  selector: 'app-inbox-bulk',
  standalone: true,
  imports: [DecimalPipe, ButtonDirective, PostPreviewOverlayComponent, DestinationPickerComponent, InboxMediaComponent],
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
  private readonly damebooru = inject(DamebooruService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly activePostId = this.store.selectedPostId;
  /** Set when a whole group is selected. */
  readonly activeGroup = signal<string | null>(null);
  readonly showHelp = signal(false);

  private readonly picker = viewChild(DestinationPickerComponent);
  private lastActiveIndex = 0;

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
        label: key === '' ? 'Unsure' : groupLocations(key).map(l => this.store.label(l)).join(' + '),
        posts,
      }))
      // Unsure posts go last; they need a decision per post.
      .sort((a, b) => Number(a.key === '') - Number(b.key === '') || b.posts.length - a.posts.length || a.label.localeCompare(b.label));
    return { groups, ordered: groups.flatMap(g => g.posts), pending };
  });

  readonly activeRows = computed(() => {
    const group = this.activeGroup();
    if (group !== null) {
      return this.view().groups.find(g => g.key === group)?.posts ?? [];
    }

    const post = this.view().ordered.find(p => p.id === this.activePostId());
    return post ? [post] : [];
  });
  readonly activeIds = computed(() => new Set(this.activeRows().map(p => p.id)));
  readonly commonDestinations = computed(() => {
    const [first, ...rest] = this.activeRows().map(p => this.store.draftOf(p.id));
    return (first ?? []).filter(l => rest.every(d => d.some(o => folderKey(o) === folderKey(l))));
  });
  readonly canAccept = computed(() => {
    return !this.store.busy() && this.activeRows().some(p => this.store.draftOf(p.id).length > 0);
  });
  readonly preview = computed(() => this.activeRows()[0] ?? null);
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

        if (this.activeGroup() !== null && this.activeRows().length > 0) return;
        this.activeGroup.set(null);
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

  predictNew(): void {
    const needed = this.store.needsPrediction();
    this.store.predict(this.imagePosts().filter(p => needed.has(p.id)).map(p => p.id));
  }

  /** For when the predictions are stale, e.g. after switching the tagger's model. */
  retagAll(): void {
    this.store.predict(this.imagePosts().map(p => p.id));
  }

  activate(post: DamebooruPostDto): void {
    this.activeGroup.set(null);
    this.activePostId.set(post.id);
    queueMicrotask(() => this.host.nativeElement.querySelector(`[data-post-id="${post.id}"]`)?.scrollIntoView({ block: 'nearest' }));
  }

  activateGroup(group: Group): void {
    this.activeGroup.set(group.key);
    this.activePostId.set(group.posts[0]?.id ?? null);
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

  discardSelection(): void {
    this.store.confirmDiscard(this.activeRows()).pipe(takeUntilDestroyed(this.destroyRef)).subscribe();
  }

  onKeydown(event: KeyboardEvent): void {
    if (event.defaultPrevented || this.store.busy()) return;

    if (event.ctrlKey && event.key === 'Enter') {
      event.preventDefault();
      const key = this.activeGroup() ?? this.store.groups().get(this.activePostId() ?? -1);
      const group = this.view().groups.find(g => g.key === key);
      if (group) this.acceptGroup(group);
      return;
    }

    if (event.key === 'Escape') {
      this.hoverPreview.close();
      (document.activeElement as HTMLElement | null)?.blur();
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
        this.move(event.key === 'ArrowDown' ? 1 : -1);
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

  private move(offset: number): void {
    const ordered = this.view().ordered;
    const index = ordered.findIndex(p => p.id === this.activePostId());
    // From a selected group, Down goes to its first post rather than past it.
    const from = index - (this.activeGroup() !== null && offset > 0 ? 1 : 0);
    const next = ordered[Math.max(0, Math.min(from + offset, ordered.length - 1))];
    if (next) this.activate(next);
  }
}
