import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { type HttpErrorResponse } from '@angular/common/http';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { type Observable, catchError, finalize, map, of, switchMap } from 'rxjs';

import { DamebooruService } from '@services/api/damebooru/damebooru.service';
import { ConfirmService } from '@services/confirm.service';
import { STORAGE_KEYS, StorageService } from '@services/storage.service';
import { ToastService } from '@services/toast.service';
import { getFileNameFromPath } from '@shared/utils/utils';
import {
  type DamebooruPostDto,
  type DestinationFolder,
  type FolderLocation,
  type FolderSuggestion,
  type PostFolderSuggestions,
  type SortInboxPost,
} from '@models';

const PAGE_SIZE = 500;
const SUGGESTION_BATCH_SIZE = 50;
const MAX_RECENT = 10;

/** Suggestions at least this sure become a post's destinations without asking. */
const CONFIDENT_SCORE = 0.7;
// Can't appear in a folder name.
const GROUP_SEPARATOR = '\u0000';

/** The bulk group for posts whose inbox file duplicates one already in the library. */
export const ALREADY_IN_LIBRARY_GROUP = 'already-in-library';

export type PredictionState = 'queued' | 'running';

export function folderKey(location: FolderLocation): string {
  return `${location.libraryId}:${location.folder}`;
}

/** Adds a folder to a destination list. A folder replaces its own ancestors, so a character folder takes the place of its series folder. */
export function withFolder(locations: FolderLocation[], location: FolderLocation): FolderLocation[] {
  const key = folderKey(location);
  if (locations.some(l => folderKey(l) === key)) return locations;
  const isAncestor = (l: FolderLocation) => l.libraryId === location.libraryId && (l.folder === '' || location.folder.startsWith(`${l.folder}/`));
  return [...locations.filter(l => !isAncestor(l)), { libraryId: location.libraryId, folder: location.folder }];
}

export function groupLocations(group: string): FolderLocation[] {
  return group === '' ? [] : group.split(GROUP_SEPARATOR).map(key => {
    const separator = key.indexOf(':');
    return { libraryId: Number(key.slice(0, separator)), folder: key.slice(separator + 1) };
  });
}

function groupOf(locations: FolderLocation[]): string {
  return locations.map(folderKey).sort().join(GROUP_SEPARATOR);
}

/** State shared by the inbox tabs: unsorted posts, where they can go, and what the server suggests. */
@Injectable()
export class InboxStore {
  private readonly damebooru = inject(DamebooruService);
  private readonly toast = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly confirmService = inject(ConfirmService);
  private readonly storage = inject(StorageService);

  readonly posts = signal<DamebooruPostDto[]>([]);
  readonly totalCount = signal(0);
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly destinations = signal<DestinationFolder[]>([]);
  readonly suggestions = signal<ReadonlyMap<number, FolderSuggestion[]>>(new Map());
  readonly recent = signal<FolderLocation[]>(this.storage.getJson<FolderLocation[]>(STORAGE_KEYS.INBOX_RECENT_DESTINATIONS) ?? []);
  /** The post both tabs show as selected, so switching tabs keeps your place. */
  readonly selectedPostId = signal<number | null>(null);

  /** Where each post goes when accepted. Starts as its confident suggestions; both tabs edit it. */
  readonly drafts = signal<ReadonlyMap<number, FolderLocation[]>>(new Map());
  /**
   * The bulk group a post is listed under, from its first destinations; '' is unsure.
   * Edits leave it alone so rows don't jump around, except clearing all destinations, which makes the post unsure.
   */
  readonly groups = signal<ReadonlyMap<number, string>>(new Map());
  private readonly editedDrafts = new Set<number>();
  /** Where posts that are already in the library live. */
  readonly existingLocations = signal<ReadonlyMap<number, FolderLocation[]>>(new Map());

  /** Images the AI tagger hasn't seen yet. */
  readonly needsPrediction = signal<ReadonlySet<number>>(new Set());
  /** Posts waiting for, or going through, the AI tagger. It runs one at a time. */
  readonly predictions = signal<ReadonlyMap<number, PredictionState>>(new Map());

  readonly unpredictedCount = computed(() => {
    const needed = this.needsPrediction();
    return this.posts().filter(p => needed.has(p.id)).length;
  });
  readonly predictingCount = computed(() => this.predictions().size);

  private readonly requestedSuggestions = new Set<number>();

  readonly libraryNames = computed(() => new Map(this.destinations().map(d => [d.libraryId, d.libraryName])));
  readonly showLibraryNames = computed(() => this.libraryNames().size > 1);

  load(): void {
    this.loading.set(true);
    this.damebooru.getPosts('inbox:true sort:old', 0, PAGE_SIZE)
      .pipe(finalize(() => this.loading.set(false)), takeUntilDestroyed(this.destroyRef))
      .subscribe(result => {
        this.posts.set(result.items);
        this.totalCount.set(result.totalCount);
      });

    this.damebooru.getInboxDestinations()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(destinations => this.destinations.set(destinations));
  }

  /** Fetches suggestions for posts that don't have them yet. */
  requestSuggestions(postIds: number[]): void {
    const missing = postIds.filter(id => !this.requestedSuggestions.has(id));
    for (let start = 0; start < missing.length; start += SUGGESTION_BATCH_SIZE) {
      const batch = missing.slice(start, start + SUGGESTION_BATCH_SIZE);
      batch.forEach(id => this.requestedSuggestions.add(id));
      this.damebooru.getFolderSuggestions(batch)
        .pipe(
          catchError(() => {
            batch.forEach(id => this.requestedSuggestions.delete(id));
            return of([]);
          }),
          takeUntilDestroyed(this.destroyRef),
        )
        .subscribe(results => this.applySuggestions(results));
    }
  }

  /** Queues posts for the AI tagger, tagged before or not. */
  predict(postIds: number[]): void {
    const current = this.predictions();
    const added = postIds.filter(id => !current.has(id));
    if (added.length === 0) return;

    this.predictions.update(state => {
      const next = new Map(state);
      added.forEach(id => next.set(id, 'queued'));
      return next;
    });
    this.runNextPrediction();
  }

  cancelPredictions(): void {
    this.predictions.update(state => new Map([...state].filter(([, s]) => s === 'running')));
  }

  private runNextPrediction(): void {
    const state = this.predictions();
    if ([...state.values()].includes('running')) return;

    const next = [...state].find(([, s]) => s === 'queued')?.[0];
    if (next === undefined) return;

    this.predictions.update(current => new Map(current).set(next, 'running'));
    const finish = () => {
      this.predictions.update(current => {
        const updated = new Map(current);
        updated.delete(next);
        return updated;
      });
      this.runNextPrediction();
    };

    this.damebooru.predictInboxPost(next)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: result => {
          this.applySuggestions([result]);
          finish();
        },
        error: (error: HttpErrorResponse) => {
          // The error interceptor already reported it. A tagger that is down would fail the rest of the queue too.
          if (error.status === 503) {
            this.predictions.set(new Map());
          } else {
            finish();
          }
        },
      });
  }

  draftOf(postId: number): FolderLocation[] {
    return this.drafts().get(postId) ?? [];
  }

  addDestination(postIds: number[], location: FolderLocation): void {
    this.setDrafts(postIds.map(id => [id, withFolder(this.draftOf(id), location)]));
  }

  removeDestination(postIds: number[], location: FolderLocation): void {
    const key = folderKey(location);
    this.setDrafts(postIds.map(id => [id, this.draftOf(id).filter(l => folderKey(l) !== key)]));
  }

  /** Makes the posts unsure, e.g. a wrong guess in an otherwise right group. */
  clearDestinations(postIds: number[]): void {
    this.setDrafts(postIds.map(id => [id, []]));
  }

  /** Asks first, then deletes the posts' inbox files. Emits how many were deleted. */
  confirmDiscard(posts: DamebooruPostDto[]): Observable<number> {
    if (posts.length === 0 || this.busy()) return of(0);
    return this.confirmService.confirm({
      title: 'Delete from inbox',
      message: posts.length === 1
        ? `Delete "${getFileNameFromPath(posts[0].relativePath)}" from the inbox? The file is deleted from disk.`
        : `Delete ${posts.length} files from the inbox? They are deleted from disk.`,
      confirmText: 'Delete',
      variant: 'danger',
    }).pipe(switchMap(confirmed => confirmed ? this.discard(posts.map(p => p.id)) : of(0)));
  }

  private setDrafts(entries: [number, FolderLocation[]][]): void {
    entries.forEach(([id]) => this.editedDrafts.add(id));
    this.drafts.update(current => {
      const next = new Map(current);
      entries.forEach(([id, locations]) => next.set(id, locations));
      return next;
    });

    const cleared = entries.filter(([, locations]) => locations.length === 0);
    if (cleared.length > 0) {
      this.groups.update(current => {
        const next = new Map(current);
        cleared.forEach(([id]) => next.set(id, ''));
        return next;
      });
    }
  }

  private applySuggestions(results: PostFolderSuggestions[]): void {
    if (results.length === 0) return;
    const seeded = results
      .filter(r => !this.editedDrafts.has(r.postId))
      .map(r => [r.postId, r.suggestions.filter(s => s.score >= CONFIDENT_SCORE).reduce<FolderLocation[]>(withFolder, [])] as const);

    this.suggestions.update(current => {
      const next = new Map(current);
      results.forEach(r => next.set(r.postId, r.suggestions));
      return next;
    });
    this.drafts.update(current => {
      const next = new Map(current);
      seeded.forEach(([id, initial]) => next.set(id, initial));
      return next;
    });
    const existing = new Map(results.filter(r => r.existingLocations.length > 0).map(r => [r.postId, r.existingLocations]));
    this.existingLocations.update(current => {
      const next = new Map(current);
      results.forEach(r => existing.has(r.postId) ? next.set(r.postId, r.existingLocations) : next.delete(r.postId));
      return next;
    });
    this.groups.update(current => {
      const next = new Map(current);
      seeded.forEach(([id, initial]) => next.set(id, existing.has(id) ? ALREADY_IN_LIBRARY_GROUP : groupOf(initial)));
      return next;
    });
    this.needsPrediction.update(current => {
      const next = new Set(current);
      results.forEach(r => r.needsPrediction ? next.add(r.postId) : next.delete(r.postId));
      return next;
    });
  }

  isRegularLibrary(libraryId: number): boolean {
    return this.libraryNames().has(libraryId);
  }

  label(location: FolderLocation): string {
    const folder = location.folder || '(root)';
    return this.showLibraryNames()
      ? `${this.libraryNames().get(location.libraryId) ?? '?'} / ${folder}`
      : folder;
  }

  /** Emits how many posts were sorted. */
  sort(items: SortInboxPost[]): Observable<number> {
    this.busy.set(true);
    return this.damebooru.sortInboxPosts(items).pipe(
      map(results => {
        const sorted = results.filter(r => !r.error);
        results.filter(r => r.error).forEach(r => this.toast.error(r.error!));
        results.filter(r => r.warning).forEach(r => this.toast.warning(r.warning!));
        const sortedIds = new Set(sorted.map(r => r.postId));
        this.removePosts([...sortedIds]);
        this.remember(items.filter(i => sortedIds.has(i.postId)).flatMap(i => i.destinations));
        this.addDestinations(items.flatMap(i => i.destinations));
        return sorted.length;
      }),
      catchError(() => of(0)),
      finalize(() => this.busy.set(false)),
    );
  }

  discard(postIds: number[]): Observable<number> {
    this.busy.set(true);
    return this.damebooru.discardInboxPosts(postIds).pipe(
      map(results => {
        results.filter(r => r.error).forEach(r => this.toast.error(r.error!));
        const discarded = results.filter(r => !r.error).map(r => r.postId);
        this.removePosts(discarded);
        return discarded.length;
      }),
      catchError(() => of(0)),
      finalize(() => this.busy.set(false)),
    );
  }

  private removePosts(postIds: number[]): void {
    if (postIds.length === 0) return;
    const removed = new Set(postIds);
    this.posts.update(posts => posts.filter(p => !removed.has(p.id)));
    this.predictions.update(state => new Map([...state].filter(([id, s]) => s === 'running' || !removed.has(id))));
    this.totalCount.update(count => count - removed.size);
  }

  /** New folders exist once something is sorted into them, so they become searchable right away. */
  private addDestinations(locations: FolderLocation[]): void {
    const known = new Set(this.destinations().map(folderKey));
    const added = locations.filter(l => !known.has(folderKey(l)));
    if (added.length === 0) return;
    this.destinations.update(current => [
      ...current,
      ...added.map(l => ({ ...l, libraryName: this.libraryNames().get(l.libraryId) ?? '', postCount: 0 })),
    ]);
  }

  private remember(locations: FolderLocation[]): void {
    const keys = new Set(locations.map(folderKey));
    const next = [
      ...locations.filter((l, i) => locations.findIndex(o => folderKey(o) === folderKey(l)) === i),
      ...this.recent().filter(l => !keys.has(folderKey(l))),
    ].slice(0, MAX_RECENT);
    this.recent.set(next);
    this.storage.setJson(STORAGE_KEYS.INBOX_RECENT_DESTINATIONS, next);
  }
}

