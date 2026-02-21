# Damebooru Server Architecture (As Built)

This document describes the architecture that currently exists in code, including known quirks.

## Runtime Components
- **`Damebooru.Server`**
  - ASP.NET Core API host
  - Registers controllers, CORS, Swagger, static thumbnail serving
  - Registers `Damebooru.Processing` services (jobs, scheduler, ingestion pipeline)
  - Auto-runs EF migrations at startup

## Data Layer
- **DB**: SQLite via EF Core (`DamebooruDbContext`)
- **Main entities**:
  - `Library`
  - `Post`
  - `Tag`, `TagCategory`, `PostTag`
  - `JobExecution`, `ScheduledJob`
  - `DuplicateGroup`, `DuplicateGroupEntry`
  - `ExcludedFile`

Selected indexing/constraints currently in place:
- `Post` index on `ContentHash`
- `Post` composite index on `(LibraryId, RelativePath)`
- unique `Tag.Name`
- unique `ExcludedFile (LibraryId, RelativePath)`

## Processing Pipeline (Current Flow)
`RecursiveScanner` delegates directory processing to `PipelineProcessor`.

### Phase 1: Discovery and scan
- Count candidate files through `IMediaSource.CountAsync`
- Stream files from `IMediaSource.GetItemsAsync`
- Filter by supported extensions (`SupportedMedia`)
- Compare against in-memory snapshots of existing posts and excluded paths

### Phase 2: Hashing and ingestion
- New/changed files are hashed via `IHasherService.ComputeContentHashAsync`
- Current implementation uses xxHash64 over file size + first/last chunk (`ContentHasher`)
- New posts are queued to `ChannelPostIngestionService` and flushed to DB in batches

### Phase 3: Change/orphan handling
- Changed files update hash/size/mtime and reset enrichment fields (width/height/perceptual hash)
- Missing files on disk are removed from DB as orphaned posts

## Media Enrichment and External Tooling
Media processing is FFmpeg-centric.

- `IImageProcessor` implementation: `FFmpegProcessor`
  - Metadata via FFprobe
  - Thumbnails via FFmpeg (images + videos)
- `ISimilarityService` implementation: `ImageHashService`
  - dHash-style perceptual hash generated from FFmpeg-decoded grayscale frames

Operational dependency: FFmpeg/FFprobe must be installed and resolvable on `PATH`.

## Job System and Scheduling Model
### Job orchestration
`IJobService` (`JobService`) manages:
- Available jobs
- Active in-memory job state (progress/message)
- Cancellation
- Persisted run history (`JobExecution`)

### Schedules
`SchedulerService`:
- polls every 30 seconds
- executes enabled due schedules
- updates `LastRun` and computes next occurrence from cron expression

Default schedules are seeded in DB (disabled by default) if missing:
- Scan All Libraries
- Generate Thumbnails
- Extract Metadata
- Compute Similarity
- Find Duplicates

## Duplicate Detection Algorithm
Implemented in `FindDuplicatesJob`:

1. Load posts and clear old unresolved duplicate groups.
2. Build exact duplicate groups by identical stored content hash (`ContentHash` field).
3. Build perceptual groups:
   - pairwise hamming distance over 64-bit perceptual hashes
   - union-find to merge connected pairs into groups
   - similarity percentage computed from distance
4. Persist groups to `DuplicateGroups` + `DuplicateGroupEntries`.

Resolution API (`DuplicatesController`) supports:
- keep all
- keep one (remove other posts from booru, keep disk files, add exclusions)
- bulk resolve all exact groups

## API Surface (High-Level)
Controllers currently exposed:
- `LibrariesController`
- `PostsController`
- `TagCategoriesController`
- `JobsController`
- `DuplicatesController`
- `SystemController`
- `AdminController` (legacy job endpoints)

## Known Architectural Quirks
- **Hash semantics**: exact-duplicate matching currently uses a fast xxHash64-based partial content hash, not a full-file cryptographic hash.
- **Path assumptions still matter**: defaults are now relative, but deployments should still override storage/database locations explicitly.
- **Old/unused traces**: comments/package refs still mention SignalR, but no active hub wiring is present.

## WIP Boundaries (Current Reality)
- Authentication and authorization are not implemented.
- Client-expected upload/create/edit API coverage is incomplete.
- Tag and tag-category management is only partially implemented.
- Large parts of UI/API behavior are still under active iteration.
