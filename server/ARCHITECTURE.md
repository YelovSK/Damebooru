# Architecture & Implementation Details

## Goal
Build a self-hosted booru image board backend that manages existing file libraries, providing tagging, search, and similarity detection.

## Technology Stack
- **Server Framework:** ASP.NET Core 10 (LTS) Web API
- **Database:** SQLite (via Entity Framework Core 10)
- **Image Processing:** ImageSharp (Abstracted via `IImageProcessor`)
- **Hashing:** MD5/SHA256 (Deduplication), PHash (Similarity)

## Architecture Overview

### 1. Core Services
The system uses interfaces to allow swapping implementations easily.
- **IScannerService:** Handles file system traversal.
- **IHasherService:** Computes file hashes (MD5).
- **ISimilarityService:** Computes perceptual hashes (Difference Hash).
- **IImageProcessor:** Generates thumbnails and extracts metadata.

### 2. Database Schema
- **Library**: Represents a root folder to scan.
- **Post**: Represents a file (image/video). Contains MD5 and Perceptual hashes.
- **Tag / TagCategory**: Metadata for posts.
- **PostTag**: Many-to-many relationship.

### 3. Scanning logic
- recursively scans configured libraries.
- ignores non-supported extensions.
- computes hashes to avoid re-processing unless changed (todo: change detection via file time/size).
- extracts metadata (width, height).

## Future Considerations
- Video thumbnail generation via FFMpeg.
- Advanced tag implications.
- Client implementation (Angular).
