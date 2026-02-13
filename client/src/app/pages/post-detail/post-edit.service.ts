import { Injectable, signal, computed, inject, DestroyRef } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Observable, of, forkJoin, catchError, tap, switchMap, map } from 'rxjs';
import { BakabooruService } from '@services/api/bakabooru/bakabooru.service';
import { AutoTaggingService } from '@services/auto-tagging/auto-tagging.service';
import { ToastService } from '@services/toast.service';
import { BakabooruPostDto, UpdatePostMetadata, ManagedTagCategory } from '@models';
import { AutoTaggingResult, TaggingStatus } from '@services/auto-tagging/models';

export interface PostEditState {
  sources: string[];
  tags: string[];
}

@Injectable()
export class PostEditService {
  private readonly bakabooru = inject(BakabooruService);
  private readonly autoTagging = inject(AutoTaggingService);
  private readonly toast = inject(ToastService);

  private originalPost = signal<BakabooruPostDto | null>(null);
  private editState = signal<PostEditState | null>(null);

  isEditing = signal(false);
  isSaving = signal(false);
  taggingStatus = signal<TaggingStatus>('idle');
  autoTags = signal<AutoTaggingResult[]>([]);

  currentState = computed(() => this.editState());

  isDirty = computed(() => {
    const original = this.originalPost();
    const current = this.editState();
    if (!original || !current) return false;

    const originalTags = original.tags.map(t => t.name).sort();
    const currentTags = [...current.tags].sort();
    const originalSources = original.sources || [];

    return (
      JSON.stringify(originalSources) !== JSON.stringify(current.sources) ||
      JSON.stringify(originalTags) !== JSON.stringify(currentTags)
    );
  });

  startEditing(post: BakabooruPostDto) {
    this.originalPost.set(post);
    this.editState.set({
      sources: post.sources || [],
      tags: post.tags.map(t => t.name),
    });
    this.isEditing.set(true);
    this.autoTags.set([]);
    this.taggingStatus.set('idle');
  }

  cancelEditing() {
    this.isEditing.set(false);
    this.editState.set(null);
    this.autoTags.set([]);
    this.taggingStatus.set('idle');
  }

  setSources(sources: string[]) {
    this.editState.update(state => state ? { ...state, sources } : null);
  }

  setTags(tags: string[]) {
    this.editState.update(state => state ? { ...state, tags } : null);
  }

  addTag(tag: string) {
    const normalized = tag.trim().toLowerCase();
    if (!normalized) return;

    this.editState.update(state => {
      if (!state || state.tags.includes(normalized)) return state;
      return { ...state, tags: [...state.tags, normalized] };
    });
  }

  removeTag(tag: string) {
    this.editState.update(state => {
      if (!state) return state;
      return { ...state, tags: state.tags.filter(t => t !== tag) };
    });
  }

  applyAutoTags(providerId: string) {
    const result = this.autoTags().find(r => r.providerId === providerId);
    if (!result) return;

    this.editState.update(state => {
      if (!state) return state;
      const newTags = new Set(state.tags);
      for (const ct of result.categorizedTags) {
        newTags.add(ct.name.toLowerCase());
      }
      return { ...state, tags: Array.from(newTags) };
    });
  }

  applyAutoSources(providerId: string) {
    const result = this.autoTags().find(r => r.providerId === providerId);
    if (!result?.sources || result.sources.length === 0) return;

    this.editState.update(state => {
      if (!state) return state;
      const newSources = new Set(state.sources);
      for (const source of result.sources!) {
        newSources.add(source);
      }
      return { ...state, sources: Array.from(newSources) };
    });
  }

  triggerAutoTagging(file: File, destroyRef: DestroyRef) {
    if (this.taggingStatus() === 'tagging') return;

    this.taggingStatus.set('tagging');
    this.autoTagging.queue(file)
      .pipe(
        tap(entry => {
          if (entry.status === 'completed' && entry.results) {
            this.autoTags.set(entry.results);
            this.taggingStatus.set('completed');
          } else if (entry.status === 'failed') {
            this.taggingStatus.set('failed');
          }
        }),
        catchError(() => {
          this.taggingStatus.set('failed');
          return of(null);
        }),
        takeUntilDestroyed(destroyRef),
      )
      .subscribe();
  }

  triggerProviderAutoTagging(file: File, providerId: string, destroyRef: DestroyRef) {
    if (this.taggingStatus() === 'tagging') return;

    const provider = this.autoTagging.getProviders().find(p => p.id === providerId);
    if (!provider) return;

    this.taggingStatus.set('tagging');
    this.autoTagging.queueWith(file, provider)
      .pipe(
        tap(entry => {
          if (entry.status === 'completed' && entry.results) {
            const result = entry.results.find(r => r.providerId === providerId);
            if (result) {
              this.autoTags.update(tags => {
                const index = tags.findIndex(t => t.providerId === providerId);
                if (index >= 0) {
                  const newTags = [...tags];
                  newTags[index] = result;
                  return newTags;
                }
                return [...tags, result];
              });
            }
            this.taggingStatus.set('completed');
          } else if (entry.status === 'failed') {
            this.taggingStatus.set('failed');
          }
        }),
        catchError(() => {
          this.taggingStatus.set('failed');
          return of(null);
        }),
        takeUntilDestroyed(destroyRef),
      )
      .subscribe();
  }

  getRegisteredProviders() {
    return this.autoTagging.getEnabledProviders();
  }

  save(destroyRef: DestroyRef): Observable<BakabooruPostDto | null> {
    const post = this.originalPost();
    const state = this.editState();
    if (!post || !state) return of(null);

    this.isSaving.set(true);

    const payload: UpdatePostMetadata = {};
    const originalSources = post.sources || [];
    if (JSON.stringify(originalSources) !== JSON.stringify(state.sources)) {
      payload.sources = state.sources;
    }

    const originalTags = post.tags.map(t => t.name).sort();
    const currentTags = [...state.tags].sort();
    if (JSON.stringify(originalTags) !== JSON.stringify(currentTags)) {
      payload.tags = state.tags;
    }

    return this.bakabooru.updatePost(post.id, payload).pipe(
      switchMap(updatedPost => {
        // Update tag categories from auto-tagging results
        const categorizedTags = this.collectCategorizedTags();
        if (categorizedTags.length === 0) return of(updatedPost);
        return this.updateTagCategories(categorizedTags, destroyRef).pipe(map(() => updatedPost));
      }),
      tap(updatedPost => {
        this.isSaving.set(false);
        this.isEditing.set(false);
        this.originalPost.set(updatedPost);
        this.editState.set(null);
        this.autoTags.set([]);
        this.toast.success('Post updated successfully');
      }),
      catchError(err => {
        this.isSaving.set(false);
        this.toast.error(err.error?.description || 'Failed to update post');
        return of(null);
      }),
      takeUntilDestroyed(destroyRef),
    );
  }

  private collectCategorizedTags(): { name: string; category: string }[] {
    const categorizedTags: { name: string; category: string }[] = [];
    for (const result of this.autoTags()) {
      for (const ct of result.categorizedTags) {
        if (ct.category && ct.category !== 'general') {
          categorizedTags.push({ name: ct.name, category: ct.category });
        }
      }
    }
    return categorizedTags;
  }

  private updateTagCategories(
    categorizedTags: { name: string; category: string }[],
    destroyRef: DestroyRef,
  ) {
    return this.bakabooru.getManagedTagCategories().pipe(
      switchMap(categories => {
        const updateTasks = categorizedTags.map(ct =>
          this.bakabooru.getManagedTags(ct.name, 0, 100).pipe(
            switchMap(tags => {
              const tag = tags.results.find(t => t.name.toLowerCase() === ct.name.toLowerCase());
              if (!tag) return of(null);

              const category = categories.find(c => c.name.toLowerCase() === ct.category.toLowerCase()) as ManagedTagCategory | undefined;
              if (!category) return of(null);

              return this.bakabooru.updateManagedTag(tag.id, tag.name, category.id);
            }),
            catchError(() => of(null)),
          ),
        );

        return forkJoin(updateTasks);
      }),
      takeUntilDestroyed(destroyRef),
    );
  }
}
