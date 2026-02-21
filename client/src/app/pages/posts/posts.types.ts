import { DamebooruPostDto } from '@models';

export type GridDensity = 'compact' | 'comfortable' | 'cozy';
export type PageStatus = 'idle' | 'loading' | 'ready' | 'error';

export interface GridCell {
    kind: 'post' | 'skeleton' | 'placeholder';
    post: DamebooruPostDto | null;
    trackKey: string;
}

export interface CachedPage {
    status: PageStatus;
    items: DamebooruPostDto[];
    error: unknown | null;
}

export interface RouteState {
    query: string;
    page: number | null;
    offset: number | null;
}

export interface SeparatorRow {
    kind: 'separator';
    page: number;
    rowId: string;
    startOffset: number;
}

export interface PostRow {
    kind: 'posts';
    page: number;
    rowId: string;
    rowInPage: number;
    startOffset: number;
    count: number;
}

export type VirtualRow = SeparatorRow | PostRow;

export interface VirtualRowPosition {
    page: number;
    rowOffsetInPage: number;
    pageItemCount: number;
}
