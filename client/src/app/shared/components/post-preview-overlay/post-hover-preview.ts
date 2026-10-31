import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';

import { type DamebooruPostDto } from '@models';
import { DamebooruService } from '@services/api/damebooru/damebooru.service';
import { SettingsService } from '@services/settings.service';

import { PostPreviewHoverGateService } from './post-preview-hover-gate.service';

/**
 * Shows a post in the preview overlay after the pointer rests on its tile.
 * Provide it per component; render `post()` with `app-post-preview-overlay` and wire tile mouse events to it.
 */
@Injectable()
export class PostHoverPreview {
  private readonly damebooru = inject(DamebooruService);
  private readonly settings = inject(SettingsService);
  private readonly gate = inject(PostPreviewHoverGateService);

  private readonly enabled = this.settings.enablePostPreviewOnHover;
  private readonly delayMs = computed(() => {
    const raw = this.settings.postPreviewDelayMs();
    return Number.isFinite(raw) ? Math.max(0, Math.min(5000, Math.round(raw))) : 700;
  });

  readonly post = signal<DamebooruPostDto | null>(null);

  private hoveredPostId: number | null = null;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private readonly cache = new Map<number, DamebooruPostDto>();

  constructor() {
    inject(DestroyRef).onDestroy(() => this.clearTimer());
  }

  /** Pass the post itself when it is already loaded, or its id to fetch it. */
  enter(target: DamebooruPostDto | number): void {
    if (!this.enabled() || this.gate.isSuppressed()) {
      return;
    }

    const postId = typeof target === 'number' ? target : target.id;
    if (typeof target !== 'number') {
      this.cache.set(postId, target);
    }

    this.hoveredPostId = postId;
    this.clearTimer();
    this.timer = setTimeout(() => {
      this.timer = null;
      this.show(postId);
    }, this.delayMs());
  }

  move(target: DamebooruPostDto | number): void {
    if (this.enabled() && this.gate.resumeIfSuppressed()) {
      this.enter(target);
    }
  }

  leave(event: MouseEvent): void {
    this.hoveredPostId = null;
    this.clearTimer();

    const related = event.relatedTarget as Element | null;
    if (!related?.closest('[data-preview-card]')) {
      this.post.set(null);
    }
  }

  close(): void {
    this.hoveredPostId = null;
    this.clearTimer();
    this.gate.suppressUntilPointerMove();
    this.post.set(null);
  }

  private show(postId: number): void {
    if (this.hoveredPostId !== postId) {
      return;
    }

    const cached = this.cache.get(postId);
    if (cached) {
      this.post.set(cached);
      return;
    }

    this.damebooru.getPost(postId).subscribe({
      next: post => {
        this.cache.set(postId, post);
        if (this.hoveredPostId === postId) {
          this.post.set(post);
        }
      },
      error: () => {
        if (this.hoveredPostId === postId) {
          this.post.set(null);
        }
      },
    });
  }

  private clearTimer(): void {
    if (this.timer !== null) {
      clearTimeout(this.timer);
      this.timer = null;
    }
  }
}
