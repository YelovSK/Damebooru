import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { DamebooruService } from '@services/api/damebooru/damebooru.service';
import { type DamebooruPostDto } from '@models';
import { type CachedPostSegment } from './posts.types';
import { POSTS_CACHE_SEGMENT_SIZE, POSTS_FETCH_SIZE } from './posts.constants';

interface OffsetRange {
    start: number;
    end: number;
}

@Injectable()
export class PostsRangeCacheStore {
    private static readonly MAX_CONCURRENT_RANGE_REQUESTS = 2;

    private readonly damebooru = inject(DamebooruService);
    private readonly destroyRef = inject(DestroyRef);

    private readonly segmentCacheState = signal(new Map<number, CachedPostSegment>());
    private readonly totalCountState = signal<number | null>(null);
    private readonly initialLoadingState = signal(true);

    private readonly inFlightRanges = new Set<string>();
    private readonly queuedRanges = new Map<string, OffsetRange>();

    private activeQuery = '';
    private currentOffsetHint = 0;

    readonly segmentCache = this.segmentCacheState.asReadonly();
    readonly totalCount = this.totalCountState.asReadonly();
    readonly isInitialLoading = this.initialLoadingState.asReadonly();

    reset(query: string): void {
        this.activeQuery = query;
        this.currentOffsetHint = 0;

        this.inFlightRanges.clear();
        this.queuedRanges.clear();
        this.segmentCacheState.set(new Map());

        this.totalCountState.set(null);
        this.initialLoadingState.set(true);
    }

    hasOffset(offset: number): boolean {
        const normalizedOffset = Math.max(0, Math.floor(offset));
        const totalCount = this.totalCountState();
        if (totalCount !== null && normalizedOffset >= totalCount) {
            return true;
        }

        const segment = this.segmentCacheState().get(this.offsetToSegmentIndex(normalizedOffset));
        return segment?.status === 'ready';
    }

    setCurrentOffsetHint(offset: number): void {
        if (!Number.isFinite(offset)) {
            return;
        }

        this.currentOffsetHint = Math.max(0, Math.floor(offset));
    }

    ensureAroundOffset(offset: number, force = false): void {
        if (!Number.isFinite(offset)) {
            return;
        }

        const normalizedOffset = Math.max(0, Math.floor(offset));
        const halfFetchSize = Math.floor(POSTS_FETCH_SIZE / 2);
        this.ensureWindow(normalizedOffset - halfFetchSize, normalizedOffset + halfFetchSize, force);
    }

    ensureWindow(startOffset: number, endOffset: number, force = false): void {
        if (!Number.isFinite(startOffset) || !Number.isFinite(endOffset)) {
            return;
        }

        const normalized = this.normalizeRequestedWindow(startOffset, endOffset);
        if (!normalized) {
            return;
        }

        for (const range of this.getMissingRanges(normalized.start, normalized.end, force)) {
            this.markRangeLoading(range.start, range.end);
            this.enqueueOrStartRange(range);
        }
    }

    private normalizeRequestedWindow(startOffset: number, endOffset: number): OffsetRange | null {
        const totalCount = this.totalCountState();
        let start = Math.max(0, Math.floor(startOffset));
        let end = Math.max(start + 1, Math.ceil(endOffset));

        const requestedSize = end - start;
        if (requestedSize < POSTS_FETCH_SIZE) {
            const midpoint = Math.floor((start + end) / 2);
            start = this.floorToSegmentStart(Math.max(0, midpoint - Math.floor(POSTS_FETCH_SIZE / 2)));
            end = start + POSTS_FETCH_SIZE;
        } else {
            start = this.floorToSegmentStart(start);
            end = this.ceilToSegmentEnd(end);
        }

        if (totalCount !== null) {
            if (totalCount <= 0 || start >= totalCount) {
                return null;
            }

            end = Math.min(end, this.ceilToSegmentEnd(totalCount));
        }

        if (end <= start) {
            return null;
        }

        return { start, end };
    }

    private getMissingRanges(startOffset: number, endOffset: number, force: boolean): OffsetRange[] {
        const ranges: OffsetRange[] = [];
        let currentRangeStart: number | null = null;
        let currentRangeEnd = startOffset;

        for (let segmentStart = startOffset; segmentStart < endOffset; segmentStart += POSTS_CACHE_SEGMENT_SIZE) {
            const segmentIndex = this.offsetToSegmentIndex(segmentStart);
            const segment = this.segmentCacheState().get(segmentIndex);
            const shouldLoad = force || (segment?.status !== 'ready' && segment?.status !== 'loading');

            if (shouldLoad) {
                if (currentRangeStart === null) {
                    currentRangeStart = segmentStart;
                }
                currentRangeEnd = segmentStart + POSTS_CACHE_SEGMENT_SIZE;
                continue;
            }

            if (currentRangeStart !== null) {
                this.pushMissingRangeChunks(ranges, currentRangeStart, currentRangeEnd);
                currentRangeStart = null;
            }
        }

        if (currentRangeStart !== null) {
            this.pushMissingRangeChunks(ranges, currentRangeStart, currentRangeEnd);
        }

        return ranges;
    }

    private pushMissingRangeChunks(ranges: OffsetRange[], startOffset: number, endOffset: number): void {
        for (let chunkStart = startOffset; chunkStart < endOffset; chunkStart += POSTS_FETCH_SIZE) {
            ranges.push({
                start: chunkStart,
                end: Math.min(endOffset, chunkStart + POSTS_FETCH_SIZE),
            });
        }
    }

    private enqueueOrStartRange(range: OffsetRange): void {
        const key = this.getRangeKey(range);
        if (this.inFlightRanges.has(key) || this.queuedRanges.has(key)) {
            return;
        }

        if (this.inFlightRanges.size >= PostsRangeCacheStore.MAX_CONCURRENT_RANGE_REQUESTS) {
            this.queuedRanges.set(key, range);
            return;
        }

        this.startRangeLoad(range);
    }

    private startRangeLoad(range: OffsetRange): void {
        const key = this.getRangeKey(range);
        const queryAtRequest = this.activeQuery;
        const limit = range.end - range.start;

        this.queuedRanges.delete(key);
        this.inFlightRanges.add(key);

        this.damebooru.getPosts(queryAtRequest, range.start, limit)
            .pipe(takeUntilDestroyed(this.destroyRef))
            .subscribe({
                next: data => {
                    this.inFlightRanges.delete(key);
                    if (queryAtRequest !== this.activeQuery) {
                        this.drainQueuedRanges();
                        return;
                    }

                    this.totalCountState.set(data.totalCount);
                    this.initialLoadingState.set(false);
                    this.storeRange(range.start, range.end, data.items, data.totalCount, null);
                    this.drainQueuedRanges();
                },
                error: error => {
                    this.inFlightRanges.delete(key);
                    if (queryAtRequest !== this.activeQuery) {
                        this.drainQueuedRanges();
                        return;
                    }

                    this.initialLoadingState.set(false);
                    this.markRangeError(range.start, range.end, error);
                    this.drainQueuedRanges();
                },
            });
    }

    private storeRange(startOffset: number, endOffset: number, items: readonly DamebooruPostDto[], totalCount: number, error: unknown): void {
        this.segmentCacheState.update(current => {
            const next = new Map(current);

            for (let segmentStart = startOffset; segmentStart < endOffset; segmentStart += POSTS_CACHE_SEGMENT_SIZE) {
                const segmentIndex = this.offsetToSegmentIndex(segmentStart);
                if (segmentStart >= totalCount) {
                    next.delete(segmentIndex);
                    continue;
                }

                const responseStart = segmentStart - startOffset;
                const expectedCount = Math.min(POSTS_CACHE_SEGMENT_SIZE, totalCount - segmentStart);
                next.set(segmentIndex, {
                    status: 'ready',
                    items: items.slice(responseStart, responseStart + expectedCount),
                    error,
                });
            }

            return next;
        });
    }

    private markRangeLoading(startOffset: number, endOffset: number): void {
        this.segmentCacheState.update(current => {
            const next = new Map(current);

            for (let segmentStart = startOffset; segmentStart < endOffset; segmentStart += POSTS_CACHE_SEGMENT_SIZE) {
                const segmentIndex = this.offsetToSegmentIndex(segmentStart);
                const existing = next.get(segmentIndex);
                next.set(segmentIndex, {
                    status: 'loading',
                    items: existing?.items ?? [],
                    error: null,
                });
            }

            return next;
        });
    }

    private markRangeError(startOffset: number, endOffset: number, error: unknown): void {
        this.segmentCacheState.update(current => {
            const next = new Map(current);

            for (let segmentStart = startOffset; segmentStart < endOffset; segmentStart += POSTS_CACHE_SEGMENT_SIZE) {
                const segmentIndex = this.offsetToSegmentIndex(segmentStart);
                const existing = next.get(segmentIndex);
                next.set(segmentIndex, {
                    status: 'error',
                    items: existing?.items ?? [],
                    error,
                });
            }

            return next;
        });
    }

    private drainQueuedRanges(): void {
        if (this.queuedRanges.size === 0) {
            return;
        }

        const candidates = Array.from(this.queuedRanges.values())
            .sort((left, right) => this.getDistanceFromHint(left) - this.getDistanceFromHint(right));

        for (const range of candidates) {
            if (this.inFlightRanges.size >= PostsRangeCacheStore.MAX_CONCURRENT_RANGE_REQUESTS) {
                break;
            }

            this.startRangeLoad(range);
        }
    }

    private getDistanceFromHint(range: OffsetRange): number {
        if (this.currentOffsetHint >= range.start && this.currentOffsetHint < range.end) {
            return 0;
        }

        return Math.min(
            Math.abs(this.currentOffsetHint - range.start),
            Math.abs(this.currentOffsetHint - range.end),
        );
    }

    private getRangeKey(range: OffsetRange): string {
        return `${range.start}:${range.end}`;
    }

    private floorToSegmentStart(offset: number): number {
        return this.offsetToSegmentIndex(offset) * POSTS_CACHE_SEGMENT_SIZE;
    }

    private ceilToSegmentEnd(offset: number): number {
        return Math.ceil(Math.max(0, offset) / POSTS_CACHE_SEGMENT_SIZE) * POSTS_CACHE_SEGMENT_SIZE;
    }

    private offsetToSegmentIndex(offset: number): number {
        return Math.floor(Math.max(0, offset) / POSTS_CACHE_SEGMENT_SIZE);
    }
}
