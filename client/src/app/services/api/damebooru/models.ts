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

export interface LibraryBrowseBreadcrumb {
  name: string;
  path: string;
}

export interface LibraryFolderNode {
  name: string;
  path: string;
  recursivePostCount: number;
  hasChildren: boolean;
  children: LibraryFolderNode[];
}

export interface LibraryBrowseResponse {
  libraryId: number;
  libraryName: string;
  currentPath: string;
  recursive: boolean;
  page: number;
  pageSize: number;
  totalCount: number;
  breadcrumbs: LibraryBrowseBreadcrumb[];
  childFolders: LibraryFolderNode[];
  posts: DamebooruPostDto[];
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

export interface DamebooruPagedResponse<T> {
  items?: T[];
  Items?: T[];
  totalCount?: number;
  TotalCount?: number;
}

export interface DamebooruTagDto {
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

export interface DamebooruPostDto {
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
  tags: DamebooruTagDto[];
  similarPosts: SimilarPost[];
}

export interface DamebooruPostListDto {
  items: DamebooruPostDto[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface DamebooruPostsAroundDto {
  prev: DamebooruPostDto | null;
  next: DamebooruPostDto | null;
}

export interface DamebooruSystemInfoDto {
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

export const KNOWN_JOB_KEYS = [
  "scan-all-libraries",
  "extract-metadata",
  "compute-similarity",
  "find-duplicates",
  "generate-thumbnails",
  "cleanup-orphaned-thumbnails",
  "apply-folder-tags",
  "sanitize-tag-names",
] as const;

export type KnownJobKey = (typeof KNOWN_JOB_KEYS)[number];
export type JobKey = KnownJobKey | (string & {});

export function isKnownJobKey(key: string): key is KnownJobKey {
  return (KNOWN_JOB_KEYS as readonly string[]).includes(key);
}

export type JobMode = "missing" | "all";

export interface JobState {
  activityText?: string;
  finalText?: string;
  progressCurrent?: number;
  progressTotal?: number;
  resultSchemaVersion?: number;
  resultJson?: string;
}

export interface JobInfo {
  id: string;
  executionId?: number;
  key: JobKey;
  name: string;
  status: JobStatus;
  state: JobState;
  startTime?: string;
  endTime?: string;
}

export interface JobViewModel {
  key: JobKey;
  name: string;
  description: string;
  supportsAllMode: boolean;
  isRunning: boolean;
  activeJobInfo?: JobInfo;
}

export interface JobExecution {
  id: number;
  jobKey: JobKey;
  jobName: string;
  status: JobStatus;
  startTime: string;
  endTime?: string;
  errorMessage?: string;
  state?: JobState;
}

export interface JobResult {
  executionId: number;
  jobKey: JobKey;
  schemaVersion?: number;
  resultJson: string;
}

export interface ScanAllLibrariesJobResult {
  scanned: number;
  added: number;
  updated: number;
  moved: number;
  removed: number;
}

export interface GenerateThumbnailsJobResult {
  scanned: number;
  totalCandidates: number;
  generated: number;
  failed: number;
  skipped: number;
}

export interface ExtractMetadataJobResult {
  totalPosts: number;
  processed: number;
  failed: number;
}

export interface ComputeSimilarityJobResult {
  scanned: number;
  totalCandidates: number;
  processed: number;
  failed: number;
}

export interface CleanupOrphanedThumbnailsJobResult {
  scanned: number;
  deleted: number;
  failed: number;
}

export interface ApplyFolderTagsJobResult {
  totalPosts: number;
  updatedPosts: number;
  addedTags: number;
  removedTags: number;
  skipped: number;
  failed: number;
}

export interface SanitizeTagNamesJobResult {
  totalTags: number;
  processed: number;
  renamed: number;
  merged: number;
  failed: number;
}

export interface FindDuplicatesJobResult {
  groups: number;
  exactGroups: number;
  perceptualGroups: number;
  matchedPairs: number;
  totalEntries: number;
}

export type KnownJobResult =
  | {
      type: "scan-all-libraries.v1";
      data: ScanAllLibrariesJobResult;
    }
  | {
      type: "generate-thumbnails.v1";
      data: GenerateThumbnailsJobResult;
    }
  | {
      type: "extract-metadata.v1";
      data: ExtractMetadataJobResult;
    }
  | {
      type: "compute-similarity.v1";
      data: ComputeSimilarityJobResult;
    }
  | {
      type: "cleanup-orphaned-thumbnails.v1";
      data: CleanupOrphanedThumbnailsJobResult;
    }
  | {
      type: "apply-folder-tags.v1";
      data: ApplyFolderTagsJobResult;
    }
  | {
      type: "sanitize-tag-names.v1";
      data: SanitizeTagNamesJobResult;
    }
  | {
      type: "find-duplicates.v1";
      data: FindDuplicatesJobResult;
    };

export function parseKnownJobResult(
  jobKey: JobKey,
  schemaVersion: number | undefined,
  resultJson: string | undefined,
): KnownJobResult | null {
  if (!resultJson || resultJson.trim().length === 0) {
    return null;
  }

  try {
    const parsed = JSON.parse(resultJson) as unknown;

    if (jobKey === "scan-all-libraries" && schemaVersion === 1) {
      const candidate = parsed as Partial<ScanAllLibrariesJobResult>;
      if (
        typeof candidate.scanned === "number" &&
        typeof candidate.added === "number" &&
        typeof candidate.updated === "number" &&
        typeof candidate.moved === "number" &&
        typeof candidate.removed === "number"
      ) {
        return {
          type: "scan-all-libraries.v1",
          data: {
            scanned: candidate.scanned,
            added: candidate.added,
            updated: candidate.updated,
            moved: candidate.moved,
            removed: candidate.removed,
          },
        };
      }
    }

    if (jobKey === "generate-thumbnails" && schemaVersion === 1) {
      const candidate = parsed as Partial<GenerateThumbnailsJobResult>;
      if (
        typeof candidate.scanned === "number" &&
        typeof candidate.totalCandidates === "number" &&
        typeof candidate.generated === "number" &&
        typeof candidate.failed === "number" &&
        typeof candidate.skipped === "number"
      ) {
        return { type: "generate-thumbnails.v1", data: candidate as GenerateThumbnailsJobResult };
      }
    }

    if (jobKey === "extract-metadata" && schemaVersion === 1) {
      const candidate = parsed as Partial<ExtractMetadataJobResult>;
      if (
        typeof candidate.totalPosts === "number" &&
        typeof candidate.processed === "number" &&
        typeof candidate.failed === "number"
      ) {
        return { type: "extract-metadata.v1", data: candidate as ExtractMetadataJobResult };
      }
    }

    if (jobKey === "compute-similarity" && schemaVersion === 1) {
      const candidate = parsed as Partial<ComputeSimilarityJobResult>;
      if (
        typeof candidate.scanned === "number" &&
        typeof candidate.totalCandidates === "number" &&
        typeof candidate.processed === "number" &&
        typeof candidate.failed === "number"
      ) {
        return { type: "compute-similarity.v1", data: candidate as ComputeSimilarityJobResult };
      }
    }

    if (jobKey === "cleanup-orphaned-thumbnails" && schemaVersion === 1) {
      const candidate = parsed as Partial<CleanupOrphanedThumbnailsJobResult>;
      if (
        typeof candidate.scanned === "number" &&
        typeof candidate.deleted === "number" &&
        typeof candidate.failed === "number"
      ) {
        return { type: "cleanup-orphaned-thumbnails.v1", data: candidate as CleanupOrphanedThumbnailsJobResult };
      }
    }

    if (jobKey === "apply-folder-tags" && schemaVersion === 1) {
      const candidate = parsed as Partial<ApplyFolderTagsJobResult>;
      if (
        typeof candidate.totalPosts === "number" &&
        typeof candidate.updatedPosts === "number" &&
        typeof candidate.addedTags === "number" &&
        typeof candidate.removedTags === "number" &&
        typeof candidate.skipped === "number" &&
        typeof candidate.failed === "number"
      ) {
        return { type: "apply-folder-tags.v1", data: candidate as ApplyFolderTagsJobResult };
      }
    }

    if (jobKey === "sanitize-tag-names" && schemaVersion === 1) {
      const candidate = parsed as Partial<SanitizeTagNamesJobResult>;
      if (
        typeof candidate.totalTags === "number" &&
        typeof candidate.processed === "number" &&
        typeof candidate.renamed === "number" &&
        typeof candidate.merged === "number" &&
        typeof candidate.failed === "number"
      ) {
        return { type: "sanitize-tag-names.v1", data: candidate as SanitizeTagNamesJobResult };
      }
    }

    if (jobKey === "find-duplicates" && schemaVersion === 1) {
      const candidate = parsed as Partial<FindDuplicatesJobResult>;
      if (
        typeof candidate.groups === "number" &&
        typeof candidate.exactGroups === "number" &&
        typeof candidate.perceptualGroups === "number" &&
        typeof candidate.matchedPairs === "number" &&
        typeof candidate.totalEntries === "number"
      ) {
        return { type: "find-duplicates.v1", data: candidate as FindDuplicatesJobResult };
      }
    }

    return null;
  } catch {
    return null;
  }
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
