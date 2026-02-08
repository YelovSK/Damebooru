# Bakabooru Server

A self-hosted booru image board backend built with .NET 10.
Manages existing file libraries, provides tagging, search, and similarity detection.

## Tech Stack
- **Framework**: .NET 10 (ASP.NET Core / Worker Service)
- **Database**: SQLite (via Entity Framework Core 10)
- **Image Processing**: ImageSharp
- **Video Processing**: (Planned wrappers)

## Features
- **Library Scanning**: Recursively scans directories for new files.
- **Hashing**:
  - MD5 for exact duplicate detection.
  - Perceptual Hash (Difference Hash) for visual similarity.
- **REST API**:
  - Manage Libraries and Posts.
  - Tag management.

## Project Structure
- `Bakabooru.Server`: Making the API available.
- `Bakabooru.Scanner`: Background worker for file scanning.
- `Bakabooru.Data`: EF Core context and migrations.
- `Bakabooru.Core`: Shared entities and interfaces.

For detailed architecture, see [ARCHITECTURE.md](ARCHITECTURE.md).
For setup instructions, see [SETUP.md](SETUP.md).
