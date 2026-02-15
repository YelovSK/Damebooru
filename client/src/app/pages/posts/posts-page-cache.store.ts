import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { BakabooruService } from '@services/api/bakabooru/bakabooru.service';
import { CachedPage } from './posts.types';

@Injectable()
export class PostsPageCacheStore {
    private static readonly PAGE_SIZE = 100;
    private static readonly PREFETCH_PAGE_RADIUS = 1;
    private static readonly MAX_CONCURRENT_PAGE_REQUESTS = 4;

    private readonly bakabooru = inject(BakabooruService);
    private readonly destroyRef = inject(DestroyRef);

    private readonly pageCacheState = signal(new Map<number, CachedPage>());
    private readonly totalCountState = signal<number | null>(null);
    private readonly initialLoadingState = signal(true);

    private readonly inFlightPages = new Set<number>();
    private readonly queuedPageLoads = new Set<number>();

    private activeQuery = '';
    private currentPageHint = 1;

    readonly pageCache = this.pageCacheState.asReadonly();
    readonly totalCount = this.totalCountState.asReadonly();
    readonly isInitialLoading = this.initialLoadingState.asReadonly();

    reset(query: string): void {
        this.activeQuery = query;
        this.currentPageHint = 1;

        this.inFlightPages.clear();
        this.queuedPageLoads.clear();
        this.pageCacheState.set(new Map());

        this.totalCountState.set(null);
        this.initialLoadingState.set(true);
    }

    hasPage(pageNumber: number): boolean {
        return this.pageCacheState().has(pageNumber);
    }

    setCurrentPageHint(pageNumber: number): void {
        if (!Number.isFinite(pageNumber)) {
            return;
        }

        this.currentPageHint = Math.max(1, Math.floor(pageNumber));
    }

    ensure(pageNumber: number, force = false): void {
        if (!Number.isFinite(pageNumber) || pageNumber < 1) {
            return;
        }

        const knownTotalPages = this.getTotalPages();
        if (knownTotalPages > 0 && pageNumber > knownTotalPages) {
            return;
        }

        const existing = this.pageCacheState().get(pageNumber);
        if (!force && (existing?.status === 'ready' || existing?.status === 'loading')) {
            return;
        }

        if (this.inFlightPages.has(pageNumber)) {
            return;
        }

        if (!force && this.inFlightPages.size >= PostsPageCacheStore.MAX_CONCURRENT_PAGE_REQUESTS) {
            this.queuedPageLoads.add(pageNumber);
            return;
        }

        this.queuedPageLoads.delete(pageNumber);

        const queryAtRequest = this.activeQuery;
        const knownItems = existing?.items ?? [];

        this.inFlightPages.add(pageNumber);
        this.setPageCacheEntry(pageNumber, {
            status: 'loading',
            items: knownItems,
            error: null,
        });

        const requestOffset = (pageNumber - 1) * PostsPageCacheStore.PAGE_SIZE;

        this.bakabooru.getPosts(queryAtRequest, requestOffset, PostsPageCacheStore.PAGE_SIZE)
            .pipe(takeUntilDestroyed(this.destroyRef))
            .subscribe({
                next: data => {
                    this.inFlightPages.delete(pageNumber);
                    if (queryAtRequest !== this.activeQuery) {
                        this.drainQueuedPageLoads();
                        return;
                    }

                    this.totalCountState.set(data.totalCount);
                    this.initialLoadingState.set(false);

                    this.setPageCacheEntry(pageNumber, {
                        status: 'ready',
                        items: data.items,
                        error: null,
                    });

                    this.drainQueuedPageLoads();
                },
                error: error => {
                    this.inFlightPages.delete(pageNumber);
                    if (queryAtRequest !== this.activeQuery) {
                        this.drainQueuedPageLoads();
                        return;
                    }

                    this.initialLoadingState.set(false);

                    this.setPageCacheEntry(pageNumber, {
                        status: 'error',
                        items: knownItems,
                        error,
                    });

                    this.drainQueuedPageLoads();
                },
            });
    }

    prefetch(basePages: Set<number>): void {
        if (basePages.size === 0) {
            return;
        }

        const totalPages = this.getTotalPages();
        const pagesToLoad = new Set<number>();

        for (const page of basePages) {
            const start = Math.max(1, page - PostsPageCacheStore.PREFETCH_PAGE_RADIUS);
            const end = totalPages > 0
                ? Math.min(totalPages, page + PostsPageCacheStore.PREFETCH_PAGE_RADIUS)
                : page;

            for (let value = start; value <= end; value += 1) {
                pagesToLoad.add(value);
            }
        }

        for (const page of pagesToLoad) {
            this.ensure(page);
        }
    }

    retry(pageNumber: number): void {
        this.ensure(pageNumber, true);
    }

    private setPageCacheEntry(pageNumber: number, entry: CachedPage): void {
        this.pageCacheState.update(current => {
            const next = new Map(current);
            next.set(pageNumber, entry);
            return next;
        });
    }

    private drainQueuedPageLoads(): void {
        if (this.queuedPageLoads.size === 0) {
            return;
        }

        if (this.inFlightPages.size >= PostsPageCacheStore.MAX_CONCURRENT_PAGE_REQUESTS) {
            return;
        }

        const candidates = Array.from(this.queuedPageLoads.values())
            .sort((left, right) => Math.abs(left - this.currentPageHint) - Math.abs(right - this.currentPageHint));

        for (const pageNumber of candidates) {
            if (this.inFlightPages.size >= PostsPageCacheStore.MAX_CONCURRENT_PAGE_REQUESTS) {
                break;
            }

            this.queuedPageLoads.delete(pageNumber);
            this.ensure(pageNumber);
        }
    }

    private getTotalPages(): number {
        const totalCount = this.totalCountState();
        if (totalCount === null || totalCount <= 0) {
            return 0;
        }

        return Math.ceil(totalCount / PostsPageCacheStore.PAGE_SIZE);
    }
}
