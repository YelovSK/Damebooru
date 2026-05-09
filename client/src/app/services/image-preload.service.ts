import { Injectable, inject } from '@angular/core';

import { ImagePreloadPolicyService } from './image-preload-policy.service';

export interface ImagePreloadOptions {
  concurrency?: number;
  keepDecoded?: boolean;
  replaceQueue?: boolean;
}

interface ImagePreloadQueueItem {
  url: string;
  keepDecoded: boolean;
}

@Injectable({
  providedIn: 'root',
})
export class ImagePreloadService {
  private readonly policy = inject(ImagePreloadPolicyService);
  private readonly defaultConcurrency = 4;
  private readonly maxDecodedImages = 80;

  private readonly queuedUrls = new Set<string>();
  private readonly inFlightUrls = new Set<string>();
  private readonly decodedImages = new Map<string, HTMLImageElement>();
  private queue: ImagePreloadQueueItem[] = [];
  private activeCount = 0;

  preload(urls: readonly string[], options: ImagePreloadOptions = {}): void {
    if (options.replaceQueue) {
      this.clearPending();
    }

    const requestedConcurrency = options.concurrency ?? this.defaultConcurrency;
    const plan = this.policy.plan(urls.length, requestedConcurrency);
    if (!plan.enabled) {
      this.clearPending();
      return;
    }

    const keepDecoded = options.keepDecoded ?? true;
    for (const url of urls.slice(0, plan.acceptedUrlCount)) {
      if (!url || this.decodedImages.has(url) || this.inFlightUrls.has(url) || this.queuedUrls.has(url)) {
        continue;
      }

      this.queue.push({ url, keepDecoded });
      this.queuedUrls.add(url);
    }

    this.pump(requestedConcurrency);
  }

  clearPending(): void {
    for (const item of this.queue) {
      this.queuedUrls.delete(item.url);
    }

    this.queue = [];
  }

  private pump(concurrency: number): void {
    const plan = this.policy.plan(this.queue.length, concurrency);
    if (!plan.enabled) {
      return;
    }

    const normalizedConcurrency = Math.max(1, Math.floor(plan.concurrency));
    while (this.activeCount < normalizedConcurrency && this.queue.length > 0) {
      const item = this.queue.shift();
      if (!item) {
        return;
      }

      this.queuedUrls.delete(item.url);
      if (this.decodedImages.has(item.url) || this.inFlightUrls.has(item.url)) {
        continue;
      }

      this.startPreload(item, normalizedConcurrency);
    }
  }

  private startPreload(item: ImagePreloadQueueItem, concurrency: number): void {
    const image = new Image();
    image.decoding = 'async';
    this.activeCount += 1;
    this.inFlightUrls.add(item.url);
    const startedAt = performance.now();

    const finish = async (loaded: boolean): Promise<void> => {
      const durationMs = performance.now() - startedAt;
      image.onload = null;
      image.onerror = null;
      this.inFlightUrls.delete(item.url);
      this.policy.recordLoad({ durationMs, loaded });

      if (loaded && item.keepDecoded) {
        try {
          await image.decode();
        } catch {
          // decode() can reject for browser/cache edge cases; the fetch still warmed the HTTP cache.
        }

        this.rememberDecodedImage(item.url, image);
      }

      this.activeCount -= 1;
      this.pump(concurrency);
    };

    image.onload = () => {
      void finish(true);
    };
    image.onerror = () => {
      void finish(false);
    };
    image.src = item.url;
  }

  private rememberDecodedImage(url: string, image: HTMLImageElement): void {
    this.decodedImages.delete(url);
    this.decodedImages.set(url, image);

    while (this.decodedImages.size > this.maxDecodedImages) {
      const oldestUrl = this.decodedImages.keys().next().value;
      if (oldestUrl === undefined) {
        return;
      }

      this.decodedImages.delete(oldestUrl);
    }
  }
}
