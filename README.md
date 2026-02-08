# Bakabooru

A self-hosted booru image board application.

## Project Structure

This repository is organized as a monorepo:

- **`server/`**: The backend implementation (ASP.NET Core 10).
  - Handles API, Database, and File System Scanning.
  - See [server/README.md](server/README.md) for setup and architecture details.

- **`client/`**: The frontend implementation (Angular) - *Planned/Pending*.

## Quick Start (Backend)

1. Navigate to the server folder:
   ```bash
   cd server
   ```
2. Run database migration (if needed):
   ```bash
   dotnet ef database update --project Bakabooru.Data --startup-project Bakabooru.Server
   ```
3. Start the API:
   ```bash
   dotnet run --project Bakabooru.Server
   ```

For detailed documentation, refer to the files in the `server/` directory:
- [Architecture](server/ARCHITECTURE.md)
- [Setup Guide](server/SETUP.md)
