import { Injectable, signal, computed, inject, DestroyRef } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Observable, of, forkJoin, catchError, tap, switchMap, map } from 'rxjs';
import { DamebooruService } from '@services/api/damebooru/damebooru.service';
import { AutoTaggingService } from '@services/auto-tagging/auto-tagging.service';
import { ToastService } from '@services/toast.service';
import { DamebooruPostDto, UpdatePostMetadata, ManagedTagCategory, PostTagSource } from '@models';
import { AutoTaggingResult, TaggingStatus } from '@services/auto-tagging/models';
import { areArraysEqual } from '@shared/utils/utils';

export interface PostEditTag {
  tagId?: number;
  name: string;
  source: PostTagSource;
}

export interface PostEditState {
  sources: string[];
  tags: PostEditTag[];
}

@Injectable()
export class PostEditService {
  private readonly damebooru = inject(DamebooruService);
  private readonly autoTagging = inject(AutoTaggingService);
  private readonly toast = inject(ToastService);

  private originalPost = signal<DamebooruPostDto | null>(null);
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

    const originalSources = original.sources || [];
    const originalTags = new Set(original.tags.map(t => this.getTagKey({
      tagId: t.id,
      name: t.name,
      source: t.source,
    })));
    const currentTags = new Set(current.tags.map(t => this.getTagKey(t)));

    return (
      !areArraysEqual(originalSources, current.sources) ||
      !this.areSetsEqual(originalTags, currentTags)
    );
  });

  startEditing(post: DamebooruPostDto) {
    this.originalPost.set(post);
    this.editState.set({
      sources: post.sources || [],
      tags: post.tags.map(t => ({
        tagId: t.id,
        name: t.name,
        source: t.source,
      })),
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
    const normalized = this.normalizeManualTags(tags);
    this.editState.update(state => {
      if (!state) return null;

      const nonManualTags = state.tags.filter(t => t.source !== PostTagSource.Manual);
      const manualTags = normalized.map(name => ({
        name,
        source: PostTagSource.Manual,
      }));

      return { ...state, tags: [...nonManualTags, ...manualTags] };
    });
  }

  addTag(tag: string, tagId?: number) {
    const normalized = this.normalizeTagName(tag);
    if (!normalized) return;

    this.editState.update(state => {
      if (!state) return state;

      const manualTagKey = this.getTagKey({
        name: normalized,
        source: PostTagSource.Manual,
      });
      const hasManualTag = state.tags.some(t => this.getTagKey(t) === manualTagKey);
      if (hasManualTag) {
        return state;
      }

      return {
        ...state,
        tags: [
          ...state.tags,
          {
            tagId,
            name: normalized,
            source: PostTagSource.Manual,
          },
        ],
      };
    });
  }

  removeTag(tag: PostEditTag) {
    const targetKey = this.getTagKey(tag);
    this.editState.update(state => {
      if (!state) return state;
      return {
        ...state,
        tags: state.tags.filter(t => this.getTagKey(t) !== targetKey),
      };
    });
  }

  applyAutoTags(providerId: string) {
    const result = this.autoTags().find(r => r.providerId === providerId);
    if (!result) return;

    this.editState.update(state => {
      if (!state) return state;
      const existingManualKeys = new Set(
        state.tags
          .filter(t => t.source === PostTagSource.Manual)
          .map(t => this.getTagKey(t)),
      );
      const tagsToAdd: PostEditTag[] = [];

      for (const ct of result.categorizedTags) {
        const normalized = this.normalizeTagName(ct.name);
        if (!normalized) {
          continue;
        }

        const key = this.getTagKey({
          name: normalized,
          source: PostTagSource.Manual,
        });
        if (existingManualKeys.has(key)) {
          continue;
        }

        existingManualKeys.add(key);
        tagsToAdd.push({ name: normalized, source: PostTagSource.Manual });
      }

      if (tagsToAdd.length === 0) {
        return state;
      }

      return {
        ...state,
        tags: [...state.tags, ...tagsToAdd],
      };
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

  save(destroyRef: DestroyRef): Observable<DamebooruPostDto | null> {
    const post = this.originalPost();
    const state = this.editState();
    if (!post || !state) return of(null);

    this.isSaving.set(true);

    const payload: UpdatePostMetadata = {};
    const originalSources = post.sources || [];
    if (!areArraysEqual(originalSources, state.sources)) {
      payload.sources = state.sources;
    }

    const originalTags = new Set(post.tags.map(t => this.getTagKey({
      tagId: t.id,
      name: t.name,
      source: t.source,
    })));
    const currentTags = new Set(state.tags.map(t => this.getTagKey(t)));
    if (!this.areSetsEqual(originalTags, currentTags)) {
      payload.tagsWithSources = state.tags.map(t => ({
        tagId: t.tagId,
        name: this.normalizeTagName(t.name),
        source: t.source,
      })).filter(t => !!t.name);
    }

    return this.damebooru.updatePost(post.id, payload).pipe(
      switchMap(() => this.damebooru.getPost(post.id)),
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
    return this.damebooru.getManagedTagCategories().pipe(
      switchMap(categories => {
        const updateTasks = categorizedTags.map(ct =>
          this.damebooru.getManagedTags(ct.name, 0, 100).pipe(
            switchMap(tags => {
              const tag = tags.results.find(t => t.name.toLowerCase() === ct.name.toLowerCase());
              if (!tag) return of(null);

              const category = categories.find(c => c.name.toLowerCase() === ct.category.toLowerCase()) as ManagedTagCategory | undefined;
              if (!category) return of(null);

              return this.damebooru.updateManagedTag(tag.id, tag.name, category.id);
            }),
            catchError(() => of(null)),
          ),
        );

        return forkJoin(updateTasks);
      }),
      takeUntilDestroyed(destroyRef),
    );
  }

  private normalizeTagName(tag: string): string {
    return tag.trim().toLowerCase();
  }

  private normalizeManualTags(tags: string[]): string[] {
    const seen = new Set<string>();
    const normalized: string[] = [];
    for (const tag of tags) {
      const normalizedTag = this.normalizeTagName(tag);
      if (!normalizedTag || seen.has(normalizedTag)) {
        continue;
      }

      seen.add(normalizedTag);
      normalized.push(normalizedTag);
    }

    return normalized;
  }

  private getTagKey(tag: PostEditTag): string {
    return `${tag.source}|${this.normalizeTagName(tag.name)}`;
  }

  private areSetsEqual(left: Set<string>, right: Set<string>): boolean {
    if (left.size !== right.size) {
      return false;
    }

    for (const item of left) {
      if (!right.has(item)) {
        return false;
      }
    }

    return true;
  }
}
