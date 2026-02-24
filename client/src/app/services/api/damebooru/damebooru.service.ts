import { Injectable, signal, computed, inject } from "@angular/core";
import { HttpClient, HttpParams } from "@angular/common/http";
import { Observable, of } from "rxjs";
import { catchError, finalize, map, shareReplay } from "rxjs/operators";
import { environment } from "@env/environment";
import {
  AddLibraryIgnoredPathResult,
  AuthSessionResponse,
  Library,
  LibraryIgnoredPath,
  LibraryBrowseResponse,
  LibraryFolderNode,
  PagedSearchResult,
  DamebooruPagedResponse,
  DamebooruPostDto,
  DamebooruPostListDto,
  DamebooruTagDto,
  DamebooruSystemInfoDto,
  ManagedTagCategory,
  UpdatePostMetadata,
  ManagedTag,
  DamebooruPostsAroundDto,
  Comment as DamebooruComment,
  JobViewModel,
  JobHistoryResponse,
  ScheduledJob,
  CronPreview,
  JobMode,
  JobKey,
  DuplicateGroup,
  ExcludedFile,
  ClearExcludedFilesResponse,
  SameFolderDuplicateGroup,
  DeleteSameFolderDuplicateRequest,
  ResolveSameFolderGroupRequest,
  ResolveSameFolderResponse,
  SimilarPost,
  AppLogList,
} from "./models";

@Injectable({
  providedIn: "root",
})
export class DamebooruService {
  private baseUrl = environment.apiBaseUrl;
  private mediaBaseUrl = environment.mediaBaseUrl;
  private authCheckInFlight$: Observable<boolean> | null = null;

  // Client auth state.
  authHeader = signal<string | null>(null);
  currentUser = signal<string | null>(null);
  authEnabled = signal(true);
  isLoggedIn = computed(() => !!this.currentUser());
  private authChecked = signal(false);

  private http = inject(HttpClient);

  constructor() {}

  private joinMediaUrl(path: string): string {
    const base = this.mediaBaseUrl.endsWith("/")
      ? this.mediaBaseUrl.slice(0, -1)
      : this.mediaBaseUrl;
    const suffix = path.startsWith("/") ? path : `/${path}`;
    return `${base}${suffix}`;
  }

  // --- Auth ---
  login(username: string, password: string): Observable<void> {
    return this.http
      .post<AuthSessionResponse>(`${this.baseUrl}/auth/login`, {
        username,
        password,
      })
      .pipe(
        map((response) => {
          this.currentUser.set(response.username);
          this.authEnabled.set(response.authEnabled);
          this.authChecked.set(true);
          return;
        }),
      );
  }

  logout(): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/auth/logout`, {}).pipe(
      map(() => {
        if (this.authEnabled()) {
          this.currentUser.set(null);
        }
        this.authChecked.set(true);
      }),
    );
  }

  ensureAuthState(): Observable<boolean> {
    if (this.authChecked()) {
      return of(this.isLoggedIn());
    }

    if (this.authCheckInFlight$) {
      return this.authCheckInFlight$;
    }

    this.authCheckInFlight$ = this.http
      .get<AuthSessionResponse>(`${this.baseUrl}/auth/me`)
      .pipe(
        map((response) => {
          this.currentUser.set(response.username);
          this.authEnabled.set(response.authEnabled);
          this.authChecked.set(true);
          return true;
        }),
        catchError(() => {
          this.currentUser.set(null);
          this.authEnabled.set(true);
          this.authChecked.set(true);
          return of(false);
        }),
        finalize(() => {
          this.authCheckInFlight$ = null;
        }),
        shareReplay(1),
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

  renameLibrary(id: number, name: string): Observable<void> {
    return this.http.patch<void>(`${this.baseUrl}/libraries/${id}/name`, {
      name,
    });
  }

  getLibraryIgnoredPaths(libraryId: number): Observable<LibraryIgnoredPath[]> {
    return this.http.get<LibraryIgnoredPath[]>(
      `${this.baseUrl}/libraries/${libraryId}/ignored-paths`,
    );
  }

  addLibraryIgnoredPath(
    libraryId: number,
    path: string,
  ): Observable<AddLibraryIgnoredPathResult> {
    return this.http.post<AddLibraryIgnoredPathResult>(
      `${this.baseUrl}/libraries/${libraryId}/ignored-paths`,
      { path },
    );
  }

  removeLibraryIgnoredPath(
    libraryId: number,
    ignoredPathId: number,
  ): Observable<void> {
    return this.http.delete<void>(
      `${this.baseUrl}/libraries/${libraryId}/ignored-paths/${ignoredPathId}`,
    );
  }

  getLibraryBrowse(
    libraryId: number,
    options?: {
      path?: string;
      recursive?: boolean;
      page?: number;
      pageSize?: number;
    },
  ): Observable<LibraryBrowseResponse> {
    let params = new HttpParams();

    if (options?.path) {
      params = params.set("path", options.path);
    }

    if (options?.recursive) {
      params = params.set("recursive", "true");
    }

    if (options?.page) {
      params = params.set("page", String(options.page));
    }

    if (options?.pageSize) {
      params = params.set("pageSize", String(options.pageSize));
    }

    return this.http.get<LibraryBrowseResponse>(
      `${this.baseUrl}/libraries/${libraryId}/browse`,
      { params },
    );
  }

  getLibraryFolders(
    libraryId: number,
    path = "",
  ): Observable<LibraryFolderNode[]> {
    let params = new HttpParams();
    if (path.trim().length > 0) {
      params = params.set("path", path.trim());
    }

    return this.http.get<LibraryFolderNode[]>(
      `${this.baseUrl}/libraries/${libraryId}/folders`,
      { params },
    );
  }

  // --- Posts ---
  getPosts(
    query = "",
    offset = 0,
    limit = 100,
  ): Observable<DamebooruPostListDto> {
    const page = Math.floor(offset / limit) + 1;

    let params = new HttpParams()
      .set("page", page.toString())
      .set("pageSize", limit.toString());

    if (query) {
      params = params.set("tags", query);
    }

    return this.http.get<DamebooruPostListDto>(`${this.baseUrl}/posts`, {
      params,
    });
  }

  // --- Jobs ---
  getJobs(): Observable<JobViewModel[]> {
    return this.http.get<JobViewModel[]>(`${this.baseUrl}/jobs`);
  }

  getJobHistory(pageSize = 20, page = 1): Observable<JobHistoryResponse> {
    return this.http.get<JobHistoryResponse>(
      `${this.baseUrl}/jobs/history?pageSize=${pageSize}&page=${page}`,
    );
  }

  getJobSchedules(): Observable<ScheduledJob[]> {
    return this.http.get<ScheduledJob[]>(`${this.baseUrl}/jobs/schedules`);
  }

  updateJobSchedule(
    id: number,
    schedule: Partial<ScheduledJob>,
  ): Observable<void> {
    return this.http.put<void>(
      `${this.baseUrl}/jobs/schedules/${id}`,
      schedule,
    );
  }

  previewCronExpression(
    expression: string,
    count = 5,
  ): Observable<CronPreview> {
    const params = new HttpParams()
      .set("expression", expression)
      .set("count", String(count));

    return this.http.get<CronPreview>(`${this.baseUrl}/jobs/cron-preview`, {
      params,
    });
  }

  startJob(key: JobKey, mode: JobMode = "missing"): Observable<{ jobId: string }> {
    return this.http.post<{ jobId: string }>(
      `${this.baseUrl}/jobs/${key}/start?mode=${mode}`,
      {},
    );
  }

  cancelJob(jobId: string): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/jobs/${jobId}/cancel`, {});
  }

  scanLibrary(libraryId: number): Observable<void> {
    return this.http.post<void>(
      `${this.baseUrl}/libraries/${libraryId}/scan`,
      {},
    );
  }

  getPost(id: number): Observable<DamebooruPostDto> {
    return this.http.get<DamebooruPostDto>(`${this.baseUrl}/posts/${id}`);
  }

  getThumbnailUrl(libraryId: number, contentHash: string): string {
    return this.joinMediaUrl(`/thumbnails/${libraryId}/${contentHash}.webp`);
  }

  getPostContentUrl(postId: number): string {
    return this.joinMediaUrl(`${this.baseUrl}/posts/${postId}/content`);
  }

  // --- System ---
  getGlobalInfo(): Observable<DamebooruSystemInfoDto> {
    return this.http.get<DamebooruSystemInfoDto>(`${this.baseUrl}/system/info`);
  }

  getTags(
    query = "",
    offset = 0,
    limit = 100,
  ): Observable<PagedSearchResult<DamebooruTagDto>> {
    const page = Math.floor(offset / limit) + 1;
    const params = new HttpParams()
      .set("query", query)
      .set("page", page.toString())
      .set("pageSize", limit.toString());

    return this.http
      .get<
        DamebooruPagedResponse<DamebooruTagDto>
      >(`${this.baseUrl}/tags`, { params })
      .pipe(
        map((response) => {
          const results = response.items || response.Items || [];

          return {
            query,
            offset,
            limit,
            total: response.totalCount || response.TotalCount || 0,
            results,
          };
        }),
      );
  }

  getTagCategories(): Observable<ManagedTagCategory[]> {
    return this.http.get<ManagedTagCategory[]>(`${this.baseUrl}/tagcategories`);
  }

  getComments(): Observable<PagedSearchResult<DamebooruComment>> {
    return of({ query: "", offset: 0, limit: 100, total: 0, results: [] });
  }

  getPostsAround(id: number, query = ""): Observable<DamebooruPostsAroundDto> {
    let params = new HttpParams();
    if (query) {
      params = params.set("tags", query);
    }

    return this.http.get<DamebooruPostsAroundDto>(
      `${this.baseUrl}/posts/${id}/around`,
      { params },
    );
  }

  getLibraryPostsAround(
    libraryId: number,
    postId: number,
    path = "",
  ): Observable<DamebooruPostsAroundDto> {
    let params = new HttpParams();
    if (path.trim().length > 0) {
      params = params.set("path", path.trim());
    }

    return this.http.get<DamebooruPostsAroundDto>(
      `${this.baseUrl}/libraries/${libraryId}/posts/${postId}/around`,
      { params },
    );
  }

  updatePost(id: number, metadata: UpdatePostMetadata): Observable<void> {
    return this.http.put<void>(`${this.baseUrl}/posts/${id}`, metadata);
  }

  favoritePost(id: number): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/posts/${id}/favorite`, {});
  }

  unfavoritePost(id: number): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/posts/${id}/favorite`);
  }

  // --- Tag Management ---
  getManagedTagCategories(): Observable<ManagedTagCategory[]> {
    return this.http.get<ManagedTagCategory[]>(`${this.baseUrl}/tagcategories`);
  }

  createTagCategory(
    name: string,
    color: string,
    order: number,
  ): Observable<ManagedTagCategory> {
    return this.http.post<ManagedTagCategory>(`${this.baseUrl}/tagcategories`, {
      name,
      color,
      order,
    });
  }

  updateTagCategory(
    id: number,
    name: string,
    color: string,
    order: number,
  ): Observable<void> {
    return this.http.put<void>(`${this.baseUrl}/tagcategories/${id}`, {
      name,
      color,
      order,
    });
  }

  deleteTagCategory(id: number): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/tagcategories/${id}`);
  }

  getManagedTags(
    query = "",
    offset = 0,
    limit = 200,
    sortBy?: string,
    sortDirection?: "asc" | "desc",
  ): Observable<PagedSearchResult<ManagedTag>> {
    const page = Math.floor(offset / limit) + 1;
    let params = new HttpParams()
      .set("query", query)
      .set("page", page.toString())
      .set("pageSize", limit.toString());

    if (sortBy) {
      params = params.set("sortBy", sortBy);
    }

    if (sortDirection) {
      params = params.set("sortDirection", sortDirection);
    }

    return this.http
      .get<
        DamebooruPagedResponse<ManagedTag>
      >(`${this.baseUrl}/tags`, { params })
      .pipe(
        map((response) => ({
          query,
          offset,
          limit,
          total: response.totalCount || response.TotalCount || 0,
          results: (response.items || response.Items || []) as ManagedTag[],
        })),
      );
  }

  createManagedTag(
    name: string,
    categoryId: number | null,
  ): Observable<ManagedTag> {
    return this.http.post<ManagedTag>(`${this.baseUrl}/tags`, {
      name,
      categoryId,
    });
  }

  updateManagedTag(
    id: number,
    name: string,
    categoryId: number | null,
  ): Observable<void> {
    return this.http.put<void>(`${this.baseUrl}/tags/${id}`, {
      name,
      categoryId,
    });
  }

  mergeTag(sourceTagId: number, targetTagId: number): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/tags/${sourceTagId}/merge`, {
      targetTagId,
    });
  }

  deleteManagedTag(id: number): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/tags/${id}`);
  }

  // --- Duplicates ---
  getDuplicateGroups(): Observable<DuplicateGroup[]> {
    return this.http.get<DuplicateGroup[]>(`${this.baseUrl}/duplicates`);
  }

  getResolvedDuplicateGroups(): Observable<DuplicateGroup[]> {
    return this.http.get<DuplicateGroup[]>(
      `${this.baseUrl}/duplicates/resolved`,
    );
  }

  keepAllInGroup(groupId: number): Observable<void> {
    return this.http.post<void>(
      `${this.baseUrl}/duplicates/${groupId}/keep-all`,
      {},
    );
  }

  markDuplicateGroupUnresolved(groupId: number): Observable<void> {
    return this.http.post<void>(
      `${this.baseUrl}/duplicates/${groupId}/mark-unresolved`,
      {},
    );
  }

  markAllDuplicateGroupsUnresolved(): Observable<{ unresolved: number }> {
    return this.http.post<{ unresolved: number }>(
      `${this.baseUrl}/duplicates/resolved/mark-all-unresolved`,
      {},
    );
  }

  autoResolveGroup(groupId: number): Observable<void> {
    return this.http.post<void>(
      `${this.baseUrl}/duplicates/${groupId}/auto-resolve`,
      {},
    );
  }

  excludePostFromGroup(groupId: number, postId: number): Observable<void> {
    return this.http.post<void>(
      `${this.baseUrl}/duplicates/${groupId}/exclude/${postId}`,
      {},
    );
  }

  deletePostFromGroup(groupId: number, postId: number): Observable<void> {
    return this.http.post<void>(
      `${this.baseUrl}/duplicates/${groupId}/delete/${postId}`,
      {},
    );
  }

  resolveAllExactDuplicates(): Observable<{ resolved: number }> {
    return this.http.post<{ resolved: number }>(
      `${this.baseUrl}/duplicates/resolve-all-exact`,
      {},
    );
  }

  resolveAllDuplicateGroups(): Observable<{ resolved: number }> {
    return this.http.post<{ resolved: number }>(
      `${this.baseUrl}/duplicates/resolve-all`,
      {},
    );
  }

  getExcludedFiles(): Observable<ExcludedFile[]> {
    return this.http.get<ExcludedFile[]>(`${this.baseUrl}/duplicates/excluded`);
  }

  unexcludeFile(id: number): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/duplicates/excluded/${id}`);
  }

  clearExcludedFiles(): Observable<ClearExcludedFilesResponse> {
    return this.http.delete<ClearExcludedFilesResponse>(
      `${this.baseUrl}/duplicates/excluded`,
    );
  }

  getExcludedFileContentUrl(id: number): string {
    return this.joinMediaUrl(
      `${this.baseUrl}/duplicates/excluded/${id}/content`,
    );
  }

  getSameFolderDuplicateGroups(): Observable<SameFolderDuplicateGroup[]> {
    return this.http.get<SameFolderDuplicateGroup[]>(
      `${this.baseUrl}/duplicates/same-folder`,
    );
  }

  resolveSameFolderGroup(
    request: ResolveSameFolderGroupRequest,
  ): Observable<ResolveSameFolderResponse> {
    return this.http.post<ResolveSameFolderResponse>(
      `${this.baseUrl}/duplicates/same-folder/resolve-group`,
      request,
    );
  }

  resolveAllSameFolderDuplicates(exactOnly = false): Observable<ResolveSameFolderResponse> {
    return this.http.post<ResolveSameFolderResponse>(
      `${this.baseUrl}/duplicates/same-folder/resolve-all?exactOnly=${exactOnly}`,
      {},
    );
  }

  getLogs(options?: {
    level?: string;
    category?: string;
    contains?: string;
    beforeId?: number;
    take?: number;
  }): Observable<AppLogList> {
    let params = new HttpParams();

    if (options?.level) {
      params = params.set("level", options.level);
    }

    if (options?.category) {
      params = params.set("category", options.category);
    }

    if (options?.contains) {
      params = params.set("contains", options.contains);
    }

    if (typeof options?.beforeId === "number") {
      params = params.set("beforeId", String(options.beforeId));
    }

    if (typeof options?.take === "number") {
      params = params.set("take", String(options.take));
    }

    return this.http.get<AppLogList>(`${this.baseUrl}/logs`, { params });
  }
}
