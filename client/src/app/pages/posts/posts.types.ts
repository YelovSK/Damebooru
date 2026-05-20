import { type DamebooruPostDto } from '@models';

export type GridDensity = 'compact' | 'comfortable' | 'cozy';
export type CacheStatus = 'idle' | 'loading' | 'ready' | 'error';

export interface GridCell {
    kind: 'post' | 'skeleton' | 'placeholder';
    post: DamebooruPostDto | null;
    trackKey: string;
}

export interface CachedPostSegment {
    status: CacheStatus;
    items: DamebooruPostDto[];
    error: unknown | null;
}

export interface RouteState {
    query: string;
    offset: number | null;
}
