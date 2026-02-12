import { Injectable, signal, computed, inject } from "@angular/core";
import {
    HttpClient,
    HttpParams,
} from "@angular/common/http";
import { Observable, of } from "rxjs";
import { catchError, finalize, map, shareReplay } from "rxjs/operators";
import {
    Post,
    Tag,
    TagCategory,
    User,
    PagedSearchResult,
    UnpagedSearchResult,
    GlobalInfo,
    Comment,
    Pool,
    PostsAround,
    PostField,
    UserRank,
    ImageSearchResult,
} from "../oxibooru/models";
import { environment } from "@env/environment";
import { StrictEncoder } from "../oxibooru/strict-encoder";

export interface Library {
    id: number;
    name: string;
    path: string;
    scanIntervalHours: number;
    postCount: number;
    totalSizeBytes: number;
    lastImportDate: string | null;
}

interface AuthSessionResponse {
    username: string;
    isAuthenticated: boolean;
}

@Injectable({
    providedIn: "root",
})
export class BakabooruService {
    private baseUrl = environment.apiBaseUrl;
    private authCheckInFlight$: Observable<boolean> | null = null;

    // Kept for compatibility with legacy Oxibooru-based code paths.
    authHeader = signal<string | null>(null);
    currentUser = signal<string | null>(null);
    isLoggedIn = computed(() => !!this.currentUser());
    private authChecked = signal(false);

    private http = inject(HttpClient);

    constructor() { }

    // --- Auth ---
    login(username: string, password: string): Observable<void> {
        return this.http.post<AuthSessionResponse>(`${this.baseUrl}/auth/login`, { username, password }).pipe(
            map(response => {
                this.currentUser.set(response.username);
                this.authChecked.set(true);
                return;
            })
        );
    }

    register(username: string, password: string): Observable<void> {
        return of(void 0);
    }

    logout(): Observable<void> {
        return this.http.post<void>(`${this.baseUrl}/auth/logout`, {}).pipe(
            map(() => {
                this.currentUser.set(null);
                this.authChecked.set(true);
            })
        );
    }

    ensureAuthState(): Observable<boolean> {
        if (this.authChecked()) {
            return of(this.isLoggedIn());
        }

        if (this.authCheckInFlight$) {
            return this.authCheckInFlight$;
        }

        this.authCheckInFlight$ = this.http.get<AuthSessionResponse>(`${this.baseUrl}/auth/me`).pipe(
            map(response => {
                this.currentUser.set(response.username);
                this.authChecked.set(true);
                return true;
            }),
            catchError(() => {
                this.currentUser.set(null);
                this.authChecked.set(true);
                return of(false);
            }),
            finalize(() => {
                this.authCheckInFlight$ = null;
            }),
            shareReplay(1)
        );

        return this.authCheckInFlight$;
    }

    // --- Libraries (New) ---
    getLibraries(): Observable<Library[]> {
        return this.http.get<Library[]>(`${this.baseUrl}/libraries`);
    }

    createLibrary(name: string, path: string): Observable<Library> {
        return this.http.post<Library>(`${this.baseUrl}/libraries`, { name, path });
    }

    deleteLibrary(id: number): Observable<void> {
        return this.http.delete<void>(`${this.baseUrl}/libraries/${id}`);
    }

    renameLibrary(id: number, name: string): Observable<Library> {
        return this.http.patch<Library>(`${this.baseUrl}/libraries/${id}/name`, { name });
    }

    // --- Posts ---
    getPosts(
        query = "",
        offset = 0,
        limit = 100,
        fields: PostField[] = ["id", "thumbnailUrl", "type", "score"],
    ): Observable<PagedSearchResult<Post>> {
        const page = Math.floor(offset / limit) + 1;

        let params = new HttpParams()
            .set("page", page.toString())
            .set("pageSize", limit.toString());

        if (query) {
            params = params.set("tags", query);
        }

        return this.http.get<any>(`${this.baseUrl}/posts`, { params }).pipe(
            map(response => {
                const items = response.items || response.Items || [];
                const results = items.map((dto: any) => this.mapPost(dto));
                return {
                    query,
                    offset,
                    limit,
                    total: response.totalCount || response.TotalCount || 0,
                    results
                };
            })
        );
    }

    // --- Admin & Jobs (New) ---
    getJobs(): Observable<any[]> {
        return this.http.get<any[]>(`${this.baseUrl}/admin/jobs`);
    }

    cancelJob(jobId: string): Observable<void> {
        return this.http.delete<void>(`${this.baseUrl}/admin/jobs/${jobId}`);
    }

    scanAllLibraries(): Observable<void> {
        return this.http.post<void>(`${this.baseUrl}/admin/jobs/scan-all`, {});
    }

    scanLibrary(libraryId: number): Observable<void> {
        return this.http.post<void>(`${this.baseUrl}/libraries/${libraryId}/scan`, {});
    }

    private mapPost(dto: any): Post {
        return {
            id: dto.id,
            version: '1',
            creationTime: dto.importDate,
            lastEditTime: dto.importDate,
            safety: 'safe',
            source: '',
            type: this.getTypeFromContentType(dto.contentType),
            checksum: dto.contentHash,
            contentHash: dto.contentHash,
            fileSize: 0, // Not provided by DTO yet
            canvasWidth: dto.width,
            canvasHeight: dto.height,
            contentUrl: dto.contentUrl || `${this.baseUrl}/posts/${dto.id}/content`,
            thumbnailUrl: dto.thumbnailUrl || dto.contentUrl || `${this.baseUrl}/posts/${dto.id}/content`,
            flags: [],
            tags: (dto.tags || []).map((t: any) => ({
                names: [t.name],
                category: t.categoryName || 'general',
                usages: 0
            })),
            relations: [],
            notes: [],
            user: { name: 'System', avatarUrl: '' },
            score: 0,
            ownScore: 0,
            ownFavorite: false,
            tagCount: (dto.tags || []).length,
            favoriteCount: 0,
            commentCount: 0,
            noteCount: 0,
            featureCount: 0,
            relationCount: 0,
            lastFeatureTime: null,
            favoritedBy: [],
            hasCustomThumbnail: false,
            mimeType: dto.contentType,
            comments: [],
            pools: []
        };
    }

    private getTypeFromContentType(contentType: string): any {
        if (contentType.startsWith('video/')) return 'video';
        if (contentType === 'image/gif') return 'animation';
        return 'image';
    }

    getPost(id: number): Observable<Post> {
        return this.http.get<any>(`${this.baseUrl}/posts/${id}`).pipe(
            map(dto => this.mapPost(dto))
        );
    }

    getPostContentUrl(id: number): string {
        return `${this.baseUrl}/posts/${id}/content`;
    }

    // --- Stubs for Compatibility ---
    getGlobalInfo(): Observable<GlobalInfo> {
        return this.http.get<any>(`${this.baseUrl}/system/info`).pipe(
            map(res => ({
                postCount: res.postCount,
                diskUsage: res.totalSizeBytes,
                featuredPost: null,
                featuringTime: null,
                featuringUser: null,
                serverTime: res.serverTime,
                config: {
                    name: "Bakabooru",
                    userNameRegex: ".*",
                    passwordRegex: ".*",
                    tagNameRegex: ".*",
                    tagCategoryNameRegex: ".*",
                    defaultUserRank: "regular",
                    enableSafety: false,
                    contact_email: "",
                    canSendMails: false,
                    privileges: {}
                }
            }))
        );
    }

    getTags(query = "", offset = 0, limit = 100): Observable<PagedSearchResult<Tag>> {
        return of({ query, offset, limit, total: 0, results: [] });
    }

    getTagCategories(): Observable<UnpagedSearchResult<TagCategory>> {
        return of({ results: [] });
    }

    getTag(name: string): Observable<Tag> { return of({} as Tag); }

    getPools(): Observable<PagedSearchResult<Pool>> {
        return of({ query: "", offset: 0, limit: 100, total: 0, results: [] });
    }

    getComments(): Observable<PagedSearchResult<Comment>> {
        return of({ query: "", offset: 0, limit: 100, total: 0, results: [] });
    }

    // Add other necessary stubs as empty observables or specific "not implemented" errors if critical

    getPostsAround(id: number, query = ""): Observable<PostsAround> {
        let params = new HttpParams();
        if (query) {
            params = params.set("tags", query);
        }

        return this.http.get<PostsAround>(`${this.baseUrl}/posts/${id}/around`, { params });
    }

    updatePost(id: number, metadata: any): Observable<Post> {
        return of({} as Post);
    }

    deletePost(id: number, version: string): Observable<void> {
        return of(void 0);
    }

    updateTag(name: string, tag: Partial<Tag>): Observable<Tag> {
        return of({} as Tag);
    }

    ratePost(id: number, score: number): Observable<Post> {
        return of({} as Post);
    }

    favoritePost(id: number): Observable<Post> {
        return of({} as Post);
    }

    unfavoritePost(id: number): Observable<Post> {
        return of({} as Post);
    }

    uploadFile(file: File): Observable<any> {
        // Mock upload progress
        return of({ token: "mock-token", progress: 100 });
    }

    createPost(token: string, safety: string, tags: string[], source: string): Observable<Post> {
        return of({} as Post);
    }

    reverseSearch(file: File): Observable<ImageSearchResult> {
        return of({ exactPost: null, similarPosts: [] });
    }
}
