export type Safety = "safe" | "sketchy" | "unsafe";

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
  authEnabled: boolean;
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
  source: PostTagSource;
}

export enum PostTagSource {
  Manual = 0,
  Folder = 1,
  Ai = 2,
}

export interface BakabooruPostDto {
  id: number;
  libraryId: number;
  libraryName: string | null;
  relativePath: string;
  contentHash: string;
  sizeBytes: number;
  width: number;
  height: number;
  contentType: string;
  importDate: string;
  fileModifiedDate: string;
  thumbnailLibraryId: number;
  thumbnailContentHash: string;
  isFavorite: boolean;
  sources: string[];
  tags: BakabooruTagDto[];
  similarPosts: SimilarPost[];
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

export enum JobStatus {
  Idle = 0,
  Running = 1,
  Completed = 2,
  Failed = 3,
  Cancelled = 4,
}

export type JobMode = "missing" | "all";

export interface JobState {
  phase: string;
  processed?: number;
  total?: number;
  succeeded?: number;
  failed?: number;
  skipped?: number;
  summary?: string;
}

export interface JobInfo {
  id: string;
  executionId?: number;
  name: string;
  status: JobStatus;
  state: JobState;
  startTime?: string;
  endTime?: string;
}

export interface JobViewModel {
  name: string;
  description: string;
  supportsAllMode: boolean;
  isRunning: boolean;
  activeJobInfo?: JobInfo;
}

export interface JobExecution {
  id: number;
  jobName: string;
  status: JobStatus;
  startTime: string;
  endTime?: string;
  errorMessage?: string;
  state?: JobState;
}

export interface JobHistoryResponse {
  items: JobExecution[];
  total: number;
}

export interface SimilarPost {
  id: number;
  libraryId: number;
  libraryName: string;
  relativePath: string;
  width: number;
  height: number;
  sizeBytes: number;
  contentType: string;
  thumbnailLibraryId: number;
  thumbnailContentHash: string;
  duplicateType: "exact" | "perceptual" | string;
  similarityPercent: number | null;
  groupIsResolved: boolean;
}

export interface ScheduledJob {
  id: number;
  jobName: string;
  cronExpression: string;
  isEnabled: boolean;
  lastRun?: string;
  nextRun?: string;
}

export interface CronPreview {
  isValid: boolean;
  error?: string;
  nextRuns: string[];
}

export interface UpdatePostMetadata {
  tagsWithSources?: UpdatePostTagInput[];
  sources?: string[];
  safety?: Safety;
  version?: string;
}

export interface UpdatePostTagInput {
  tagId?: number;
  name: string;
  source: PostTagSource;
}

export interface DuplicatePost {
  id: number;
  libraryId: number;
  relativePath: string;
  contentHash: string;
  width: number;
  height: number;
  contentType: string;
  sizeBytes: number;
  importDate: string;
  fileModifiedDate: string;
  thumbnailLibraryId: number;
  thumbnailContentHash: string;
}

export interface DuplicateGroup {
  id: number;
  type: "exact" | "perceptual";
  similarityPercent: number | null;
  detectedDate: string;
  posts: DuplicatePost[];
}

export interface ExcludedFile {
  id: number;
  libraryId: number;
  libraryName: string;
  relativePath: string;
  contentHash: string | null;
  excludedDate: string;
  reason: string;
}

export interface SameFolderDuplicatePost {
  id: number;
  libraryId: number;
  relativePath: string;
  contentHash: string;
  width: number;
  height: number;
  sizeBytes: number;
  importDate: string;
  fileModifiedDate: string;
  thumbnailLibraryId: number;
  thumbnailContentHash: string;
}

export interface SameFolderDuplicateGroup {
  parentDuplicateGroupId: number;
  duplicateType: "exact" | "perceptual";
  similarityPercent: number | null;
  libraryId: number;
  libraryName: string;
  folderPath: string;
  recommendedKeepPostId: number;
  posts: SameFolderDuplicatePost[];
}

export interface DeleteSameFolderDuplicateRequest {
  parentDuplicateGroupId: number;
  libraryId: number;
  folderPath: string;
  postId: number;
}

export interface ResolveSameFolderGroupRequest {
  parentDuplicateGroupId: number;
  libraryId: number;
  folderPath: string;
}

export interface ResolveSameFolderResponse {
  resolvedGroups: number;
  deletedPosts: number;
  skippedGroups: number;
}
