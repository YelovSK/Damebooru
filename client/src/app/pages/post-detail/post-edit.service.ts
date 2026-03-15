import { Injectable, signal, computed, inject, DestroyRef } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Observable, of, catchError, tap, switchMap } from 'rxjs';
import { DamebooruService } from '@services/api/damebooru/damebooru.service';
import { ToastService } from '@services/toast.service';
import { DamebooruPostDto, UpdatePostMetadata, PostTagSource } from '@models';
import { areArraysEqual } from '@shared/utils/utils';

export interface PostEditTag {
  tagId?: number;
  name: string;
  sources: PostTagSource[];
}

export interface PostEditState {
  sources: string[];
  tags: PostEditTag[];
}

@Injectable()
export class PostEditService {
  private readonly damebooru = inject(DamebooruService);
  private readonly toast = inject(ToastService);

  private originalPost = signal<DamebooruPostDto | null>(null);
  private editState = signal<PostEditState | null>(null);

  isEditing = signal(false);
  isSaving = signal(false);

  currentState = computed(() => this.editState());

  isDirty = computed(() => {
    const original = this.originalPost();
    const current = this.editState();
    if (!original || !current) return false;

    const originalSources = original.sources || [];
    const originalTags = new Set(original.tags.map(t => this.getTagKey({
      tagId: t.id,
      name: t.name,
      sources: t.sources,
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
        sources: [...t.sources],
      })),
    });
    this.isEditing.set(true);
  }

  cancelEditing() {
    this.isEditing.set(false);
    this.editState.set(null);
  }

  setSources(sources: string[]) {
    this.editState.update(state => state ? { ...state, sources } : null);
  }

  setTags(tags: string[]) {
    const normalized = this.normalizeManualTags(tags);
    this.editState.update(state => {
      if (!state) return null;

      const normalizedSet = new Set(normalized);
      const nextTags = state.tags
        .map(tag => {
          const hasManualSource = tag.sources.includes(PostTagSource.Manual);
          const shouldHaveManualSource = normalizedSet.has(tag.name);
          if (hasManualSource === shouldHaveManualSource) {
            return tag;
          }

          const nextSources = shouldHaveManualSource
            ? [...tag.sources, PostTagSource.Manual]
            : tag.sources.filter(source => source !== PostTagSource.Manual);

          return {
            ...tag,
            sources: this.normalizeSources(nextSources),
          };
        })
        .filter(tag => tag.sources.length > 0);

      for (const name of normalized) {
        if (nextTags.some(tag => tag.name === name)) {
          continue;
        }

        nextTags.push({
          name,
          sources: [PostTagSource.Manual],
        });
      }

      return { ...state, tags: nextTags };
    });
  }

  addTag(tag: string, tagId?: number) {
    const normalized = this.normalizeTagName(tag);
    if (!normalized) return;

    this.editState.update(state => {
      if (!state) return state;

      const manualTagKey = this.getTagKey({
        name: normalized,
        sources: [PostTagSource.Manual],
      });
      const hasManualTag = state.tags.some(t => this.getTagKey(t) === manualTagKey);
      if (hasManualTag) {
        return state;
      }

      return {
        ...state,
        tags: (() => {
          const existingTag = state.tags.find(t => t.name === normalized);
          if (existingTag) {
            return state.tags.map(tag => tag === existingTag
              ? { ...tag, sources: this.normalizeSources([...tag.sources, PostTagSource.Manual]) }
              : tag);
          }

          return [
            ...state.tags,
            {
              tagId,
              name: normalized,
              sources: [PostTagSource.Manual],
            },
          ];
        })(),
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
      sources: t.sources,
    })));
    const currentTags = new Set(state.tags.map(t => this.getTagKey(t)));
    if (!this.areSetsEqual(originalTags, currentTags)) {
      payload.tagsWithSources = state.tags
        .flatMap(t => this.normalizeSources(t.sources).map(source => ({
          tagId: t.tagId,
          name: this.normalizeTagName(t.name),
          source,
        })))
        .filter(t => !!t.name);
    }

    return this.damebooru.updatePost(post.id, payload).pipe(
      switchMap(() => this.damebooru.getPost(post.id)),
      tap(updatedPost => {
        this.isSaving.set(false);
        this.isEditing.set(false);
        this.originalPost.set(updatedPost);
        this.editState.set(null);
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
    return `${this.normalizeSources(tag.sources).join(',')}|${this.normalizeTagName(tag.name)}`;
  }

  private normalizeSources(sources: PostTagSource[]): PostTagSource[] {
    return Array.from(new Set(sources)).sort((a, b) => a - b);
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
