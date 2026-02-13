export type Safety = 'safe' | 'sketchy' | 'unsafe';

export interface PagedSearchResult<T> {
    query: string;
    offset: number;
    limit: number;
    total: number;
    results: T[];
}

export interface AuthSessionResponse {
    username: string;
    isAuthenticated: boolean;
}

export interface LibraryIgnoredPath {
    id: number;
    path: string;
    createdDate: string;
}

export interface Library {
    id: number;
    name: string;
    path: string;
    scanIntervalHours: number;
    postCount: number;
    totalSizeBytes: number;
    lastImportDate: string | null;
    ignoredPaths: LibraryIgnoredPath[];
}

export interface AddLibraryIgnoredPathResult {
    ignoredPath: LibraryIgnoredPath;
    removedPostCount: number;
}

export interface ManagedTagCategory {
    id: number;
    name: string;
    color: string;
    order: number;
    tagCount: number;
}

export interface ManagedTag {
    id: number;
    name: string;
    categoryId: number | null;
    categoryName: string | null;
    categoryColor: string | null;
    usages: number;
}

export interface Comment {
    id: number;
    postId: number;
    text: string;
    creationTime: string;
    lastEditTime: string;
    score: number;
    ownScore: number;
}

export interface BakabooruPagedResponse<T> {
    items?: T[];
    Items?: T[];
    totalCount?: number;
    TotalCount?: number;
}

export interface BakabooruTagDto {
    id: number;
    name: string;
    categoryId: number | null;
    categoryName: string | null;
    categoryColor: string | null;
    usages: number;
}

export interface BakabooruPostDto {
    id: number;
    libraryId: number;
    relativePath: string;
    contentHash: string;
    width: number;
    height: number;
    contentType: string;
    importDate: string;
    thumbnailUrl: string;
    contentUrl: string;
    isFavorite: boolean;
    sources: string[];
    tags: BakabooruTagDto[];
}

export interface BakabooruPostListDto {
    items: BakabooruPostDto[];
    totalCount: number;
    page: number;
    pageSize: number;
}

export interface BakabooruPostsAroundDto {
    prev: BakabooruPostDto | null;
    next: BakabooruPostDto | null;
}

export interface BakabooruSystemInfoDto {
    postCount: number;
    totalSizeBytes: number;
    tagCount: number;
    libraryCount: number;
    serverTime: string;
}

export interface BakabooruJobInfo {
    id: string;
    name: string;
    status: number;
    progress: number;
    message: string;
    startTime?: string;
    endTime?: string;
}

export interface UpdatePostMetadata {
    tags?: string[] | BakabooruTagDto[];
    source?: string;
    sources?: string[];
    safety?: Safety;
    version?: string;
}
