import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { AppLinks } from '@app/app.paths';
import { type FolderLocation } from '@models';
import { ButtonDirective } from '@shared/directives';
import { FileSizePipe } from '@shared/pipes/file-size.pipe';
import { DimensionsPipe } from '@shared/pipes/dimensions.pipe';
import { getFileNameFromPath } from '@shared/utils/utils';

import { DestinationPickerComponent } from './destination-picker.component';
import { InboxMediaComponent } from './inbox-media.component';
import { InboxStore, folderKey } from './inbox.store';

const PREFETCH_AHEAD = 10;

@Component({
  selector: 'app-inbox-review',
  standalone: true,
  imports: [DecimalPipe, RouterLink, ButtonDirective, FileSizePipe, DimensionsPipe, DestinationPickerComponent, InboxMediaComponent],
  host: {
    class: 'block flex-1 min-h-0 overflow-y-auto',
    '(keydown)': 'onKeydown($event)',
    '(document:keydown)': 'onDocumentKeydown($event)',
  },
  templateUrl: './inbox-review.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class InboxReviewComponent {
  readonly store = inject(InboxStore);
  private readonly destroyRef = inject(DestroyRef);

  readonly appLinks = AppLinks;

  readonly index = signal(0);
  readonly showHelp = signal(false);

  private readonly picker = viewChild(DestinationPickerComponent);

  readonly current = computed(() => {
    const posts = this.store.posts();
    return posts.length === 0 ? null : posts[Math.min(this.index(), posts.length - 1)];
  });
  readonly position = computed(() => Math.min(this.index(), this.store.posts().length - 1) + 1);
  readonly fileName = computed(() => getFileNameFromPath(this.current()?.relativePath ?? ''));
  readonly suggestions = computed(() => {
    const post = this.current();
    return post ? this.store.suggestions().get(post.id) : undefined;
  });
  readonly selected = computed(() => {
    const post = this.current();
    return post ? this.store.draftOf(post.id) : [];
  });
  readonly selectedKeys = computed(() => new Set(this.selected().map(folderKey)));
  readonly prediction = computed(() => {
    const post = this.current();
    return post ? this.store.predictions().get(post.id) ?? null : null;
  });
  readonly needsPrediction = computed(() => {
    const post = this.current();
    return !!post && this.store.needsPrediction().has(post.id);
  });
  readonly alreadyIn = computed(() =>
    (this.current()?.postFiles ?? []).filter(f => this.store.isRegularLibrary(f.libraryId)));

  constructor() {
    effect(() => {
      const selectedId = this.store.selectedPostId();
      const found = this.store.posts().findIndex(p => p.id === selectedId);
      if (found >= 0) untracked(() => this.index.set(found));
    });

    // A new post starts with an empty search, and the next few get their suggestions early.
    let shownPostId: number | null = null;
    effect(() => {
      const post = this.current();
      untracked(() => {
        if (post?.id === shownPostId) return;
        shownPostId = post?.id ?? null;
        if (post) this.store.selectedPostId.set(post.id);
        this.picker()?.reset();
        const posts = this.store.posts();
        const start = Math.max(0, Math.min(this.index(), posts.length - 1));
        this.store.requestSuggestions(posts.slice(start, start + PREFETCH_AHEAD).map(p => p.id));
        this.picker()?.focus();
      });
    });
  }

  retryPrediction(): void {
    const post = this.current();
    if (post) this.store.predict([post.id]);
  }

  isSelected(location: FolderLocation): boolean {
    return this.selectedKeys().has(folderKey(location));
  }

  add(location: FolderLocation): void {
    const post = this.current();
    if (post) this.store.addDestination([post.id], location);
  }

  remove(location: FolderLocation): void {
    const post = this.current();
    if (post) this.store.removeDestination([post.id], location);
  }

  toggle(location: FolderLocation): void {
    if (this.isSelected(location)) {
      this.remove(location);
    } else {
      this.add(location);
    }

    this.picker()?.focus();
  }

  go(delta: number): void {
    const count = this.store.posts().length;
    if (count === 0) return;
    this.index.update(i => Math.max(0, Math.min(count - 1, Math.min(i, count - 1) + delta)));
  }

  sort(): void {
    const post = this.current();
    const destinations = this.selected();
    if (!post || destinations.length === 0 || this.store.busy()) return;

    this.store.sort([{ postId: post.id, destinations }])
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.picker()?.focus());
  }

  discard(): void {
    const post = this.current();
    if (!post) return;

    this.store.confirmDiscard([post])
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.picker()?.focus());
  }

  /** Left/right browse posts unless they would move a text cursor or seek a video. */
  onDocumentKeydown(event: KeyboardEvent): void {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;

    const target = event.target as HTMLElement | null;
    const editsText = (target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement) && target.value !== '';
    if (target instanceof HTMLVideoElement || ((editsText || !!target?.isContentEditable) && !event.ctrlKey)) {
      return;
    }

    event.preventDefault();
    this.go(event.key === 'ArrowLeft' ? -1 : 1);
  }

  /** Keys the destination picker leaves alone while its search is empty. */
  onKeydown(event: KeyboardEvent): void {
    if (event.defaultPrevented) return;
    const target = event.target;
    if (target instanceof HTMLInputElement && target.value !== '') return;

    if (event.ctrlKey && event.key === 'Enter') {
      event.preventDefault();
      this.sort();
    } else if (event.key === 'Escape') {
      event.preventDefault();
      this.go(1);
    } else if (event.key === 'Delete') {
      event.preventDefault();
      this.discard();
    } else if (!event.ctrlKey && !event.altKey) {
      const suggestion = this.suggestions()?.[Number(event.key) - 1];
      if (/^[1-9]$/.test(event.key) && suggestion) {
        event.preventDefault();
        this.toggle(suggestion);
      }
    }
  }
}
