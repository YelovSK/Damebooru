import { Injectable, signal, computed, inject } from "@angular/core";
import {
    HttpClient,
    HttpHeaders,
    HttpParams,
} from "@angular/common/http";
import { Observable, of, forkJoin, switchMap } from "rxjs";
import { catchError, finalize, map, shareReplay } from "rxjs/operators";
import { environment } from "@env/environment";
import { AddLibraryIgnoredPathResult, AuthSessionResponse, Library, LibraryIgnoredPath, PagedSearchResult, BakabooruPagedResponse, BakabooruPostDto, BakabooruPostListDto, BakabooruJobInfo, BakabooruTagDto, BakabooruSystemInfoDto, ManagedTagCategory, UpdatePostMetadata, ManagedTag, BakabooruPostsAroundDto, Comment as BakabooruComment } from "./models";

@Injectable({
    providedIn: "root",
})
export class BakabooruService {
    private baseUrl = environment.apiBaseUrl;
    private authCheckInFlight$: Observable<boolean> | null = null;

    // Kept for compatibility with legacy Bakabooru-based code paths.
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

    getLibraryIgnoredPaths(libraryId: number): Observable<LibraryIgnoredPath[]> {
        return this.http.get<LibraryIgnoredPath[]>(`${this.baseUrl}/libraries/${libraryId}/ignored-paths`);
    }

    addLibraryIgnoredPath(libraryId: number, path: string): Observable<AddLibraryIgnoredPathResult> {
        return this.http.post<AddLibraryIgnoredPathResult>(`${this.baseUrl}/libraries/${libraryId}/ignored-paths`, { path });
    }

    removeLibraryIgnoredPath(libraryId: number, ignoredPathId: number): Observable<void> {
        return this.http.delete<void>(`${this.baseUrl}/libraries/${libraryId}/ignored-paths/${ignoredPathId}`);
    }

    // --- Posts ---
    getPosts(
        query = "",
        offset = 0,
        limit = 100,
    ): Observable<BakabooruPostListDto> {
        const page = Math.floor(offset / limit) + 1;

        let params = new HttpParams()
            .set("page", page.toString())
            .set("pageSize", limit.toString());

        if (query) {
            params = params.set("tags", query);
        }

        return this.http.get<BakabooruPostListDto>(`${this.baseUrl}/posts`, { params });
    }

    // --- Admin & Jobs (New) ---
    getJobs(): Observable<BakabooruJobInfo[]> {
        return this.http.get<BakabooruJobInfo[]>(`${this.baseUrl}/admin/jobs`);
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

    getPost(id: number): Observable<BakabooruPostDto> {
        return this.http.get<BakabooruPostDto>(`${this.baseUrl}/posts/${id}`);
    }

    getPostContentUrl(id: number): string {
        return `${this.baseUrl}/posts/${id}/content`;
    }

    // --- Stubs for Compatibility ---
    getGlobalInfo(): Observable<BakabooruSystemInfoDto> {
        return this.http.get<BakabooruSystemInfoDto>(`${this.baseUrl}/system/info`);
    }

    getTags(query = "", offset = 0, limit = 100): Observable<PagedSearchResult<BakabooruTagDto>> {
        const page = Math.floor(offset / limit) + 1;
        const params = new HttpParams()
            .set("query", query)
            .set("page", page.toString())
            .set("pageSize", limit.toString());

        return this.http.get<BakabooruPagedResponse<BakabooruTagDto>>(`${this.baseUrl}/tags`, { params }).pipe(
            map(response => {
                const results = response.items || response.Items || [];

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

    getTagCategories(): Observable<ManagedTagCategory[]> {
        return this.http.get<ManagedTagCategory[]>(`${this.baseUrl}/tagcategories`);
    }

    getComments(): Observable<PagedSearchResult<BakabooruComment>> {
        return of({ query: "", offset: 0, limit: 100, total: 0, results: [] });
    }

    // Add other necessary stubs as empty observables or specific "not implemented" errors if critical

    getPostsAround(id: number, query = ""): Observable<BakabooruPostsAroundDto> {
        let params = new HttpParams();
        if (query) {
            params = params.set("tags", query);
        }

        return this.http.get<BakabooruPostsAroundDto>(`${this.baseUrl}/posts/${id}/around`, { params });
    }

    updatePost(id: number, metadata: UpdatePostMetadata): Observable<BakabooruPostDto> {
        const desiredTagsRaw = this.normalizeUpdateTags(metadata?.tags);
        const desiredSourcesRaw = this.normalizeUpdateSources(metadata);

        if (desiredTagsRaw === null && desiredSourcesRaw === null) {
            return this.getPost(id);
        }

        const normalizeTag = (tagName: string) => tagName.trim().toLowerCase();
        const desiredTags = desiredTagsRaw === null
            ? null
            : Array.from(
                new Set(
                    desiredTagsRaw
                        .map(t => t?.trim())
                        .filter((t): t is string => !!t),
                ),
            );
        const desiredTagLookup = desiredTags === null
            ? null
            : new Set(desiredTags.map(normalizeTag));

        return this.getPost(id).pipe(
            switchMap(post => {
                const currentSources = post.sources
                    .map(s => s.trim())
                    .filter(s => !!s);

                const operations: Observable<unknown>[] = [];

                if (desiredTags !== null && desiredTagLookup !== null) {
                    const currentTags = Array.from(new Set(post.tags.map(t => t.name.trim())));
                    const currentTagLookup = new Set(currentTags.map(normalizeTag));
                    const toAdd = desiredTags.filter(t => !currentTagLookup.has(normalizeTag(t)));
                    const toRemove = currentTags.filter(t => !desiredTagLookup.has(normalizeTag(t)));

                    operations.push(
                        ...toAdd.map(tag => this.addTagToPost(id, tag)),
                        ...toRemove.map(tag => this.removeTagFromPost(id, tag)),
                    );
                }

                if (desiredSourcesRaw !== null && !this.areStringListsEqual(currentSources, desiredSourcesRaw)) {
                    operations.push(this.setPostSources(id, desiredSourcesRaw));
                }

                if (operations.length === 0) {
                    return this.getPost(id);
                }

                return forkJoin(operations).pipe(switchMap(() => this.getPost(id)));
            }),
        );
    }

    private normalizeUpdateTags(tags: UpdatePostMetadata["tags"]): string[] | null {
        if (!Array.isArray(tags)) {
            return null;
        }

        const normalized: string[] = [];
        for (const tag of tags) {
            if (typeof tag === "string") {
                normalized.push(tag);
                continue;
            }

            if (tag && typeof tag === "object") {
                if ("name" in tag && typeof tag.name === "string") {
                    normalized.push(tag.name);
                    continue;
                }

                if ("names" in tag && Array.isArray(tag.names) && tag.names.length > 0 && typeof tag.names[0] === "string") {
                    normalized.push(tag.names[0]);
                }
            }
        }

        return normalized;
    }

    private normalizeUpdateSources(metadata: UpdatePostMetadata): string[] | null {
        if (Array.isArray(metadata?.sources)) {
            return metadata.sources
                .map(s => (typeof s === "string" ? s.trim() : ""))
                .filter((s): s is string => !!s);
        }

        if (typeof metadata?.source === "string") {
            return metadata.source
                .split('\n')
                .map(s => s.trim())
                .filter((s): s is string => !!s);
        }

        return null;
    }

    private areStringListsEqual(a: string[], b: string[]): boolean {
        if (a.length !== b.length) return false;
        for (let i = 0; i < a.length; i++) {
            if (a[i] !== b[i]) return false;
        }
        return true;
    }

    favoritePost(id: number): Observable<BakabooruPostDto> {
        return this.http.post<BakabooruPostDto>(`${this.baseUrl}/posts/${id}/favorite`, {});
    }

    unfavoritePost(id: number): Observable<BakabooruPostDto> {
        return this.http.delete<BakabooruPostDto>(`${this.baseUrl}/posts/${id}/favorite`);
    }

    // --- Tag Management ---
    getManagedTagCategories(): Observable<ManagedTagCategory[]> {
        return this.http.get<ManagedTagCategory[]>(`${this.baseUrl}/tagcategories`);
    }

    createTagCategory(name: string, color: string, order: number): Observable<ManagedTagCategory> {
        return this.http.post<ManagedTagCategory>(`${this.baseUrl}/tagcategories`, { name, color, order });
    }

    updateTagCategory(id: number, name: string, color: string, order: number): Observable<ManagedTagCategory> {
        return this.http.put<ManagedTagCategory>(`${this.baseUrl}/tagcategories/${id}`, { name, color, order });
    }

    deleteTagCategory(id: number): Observable<void> {
        return this.http.delete<void>(`${this.baseUrl}/tagcategories/${id}`);
    }

    getManagedTags(query = "", offset = 0, limit = 200): Observable<PagedSearchResult<ManagedTag>> {
        const page = Math.floor(offset / limit) + 1;
        const params = new HttpParams()
            .set("query", query)
            .set("page", page.toString())
            .set("pageSize", limit.toString());

        return this.http.get<BakabooruPagedResponse<ManagedTag>>(`${this.baseUrl}/tags`, { params }).pipe(
            map(response => ({
                query,
                offset,
                limit,
                total: response.totalCount || response.TotalCount || 0,
                results: (response.items || response.Items || []) as ManagedTag[]
            }))
        );
    }

    createManagedTag(name: string, categoryId: number | null): Observable<ManagedTag> {
        return this.http.post<ManagedTag>(`${this.baseUrl}/tags`, { name, categoryId });
    }

    updateManagedTag(id: number, name: string, categoryId: number | null): Observable<ManagedTag> {
        return this.http.put<ManagedTag>(`${this.baseUrl}/tags/${id}`, { name, categoryId });
    }

    mergeTag(sourceTagId: number, targetTagId: number): Observable<void> {
        return this.http.post<void>(`${this.baseUrl}/tags/${sourceTagId}/merge`, { targetTagId });
    }

    deleteManagedTag(id: number): Observable<void> {
        return this.http.delete<void>(`${this.baseUrl}/tags/${id}`);
    }

    private addTagToPost(id: number, tagName: string): Observable<void> {
        const headers = new HttpHeaders({
            "Content-Type": "application/json",
        });
        return this.http.post<void>(`${this.baseUrl}/posts/${id}/tags`, JSON.stringify(tagName), { headers });
    }

    private removeTagFromPost(id: number, tagName: string): Observable<void> {
        return this.http.delete<void>(`${this.baseUrl}/posts/${id}/tags/${encodeURIComponent(tagName)}`);
    }

    private setPostSources(id: number, sources: string[]): Observable<void> {
        return this.http.put(`${this.baseUrl}/posts/${id}/sources`, sources).pipe(
            map(() => void 0),
        );
    }

}
