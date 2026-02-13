# Bakabooru

Bakabooru is a self-hosted booru monorepo with:
- ASP.NET Core backend (`server/`)
- Angular frontend (`client/`)

This README is the main entry point for running locally and deploying with Docker.

## Repository Layout
- `server/` - .NET backend solution (`Bakabooru.Server`, `Bakabooru.Processing`, `Bakabooru.Data`, `Bakabooru.Core`)
- `client/` - Angular frontend
- `docs/` - supporting documentation
- `docker-compose.example.yml` - example production-ish deployment with prebuilt GHCR images

## Prerequisites (Local Development)
- .NET 10 SDK
- Node.js 22+ and npm
- FFmpeg + FFprobe on `PATH` (required for thumbnails/metadata/similarity jobs)

## Configuration
Before first run, verify these settings in `server/Bakabooru.Server/appsettings.json`:
- `ConnectionStrings:DefaultConnection`
- `Bakabooru:Storage:DatabasePath`
- `Bakabooru:Storage:ThumbnailPath`
- `Bakabooru:Storage:TempPath`

Notes:
- Relative storage paths are resolved from `server/Bakabooru.Server`.
- Scheduler behavior is controlled by `Bakabooru:Processing:RunScheduler`.

## Run Locally

Backend:

```bash
cd server
dotnet run --project Bakabooru.Server
```

Frontend:

```bash
cd client
npm install
npm start
```

### Database Migrations
Migrations are auto-applied when `Bakabooru.Server` starts.

Optional manual command:

```bash
cd server
dotnet ef database update --project Bakabooru.Data --startup-project Bakabooru.Server
```

## Deploy with Docker

### Option A: Use Published Images from GHCR
1. Copy the example compose file:

```bash
cp docker-compose.example.yml docker-compose.yml
```

2. Edit values as needed:
- `Bakabooru__Auth__Username` / `Bakabooru__Auth__Password`
- volume mounts (`./data/server`, `./media`)
- client port mapping (`8080:80`)

3. Start:

```bash
docker compose up -d
```

Open the app at `http://localhost:8080` (or your mapped host port).

### Option B: Build Images Locally
From repo root:

```bash
docker build -t bakabooru-server ./server
docker build -t bakabooru-client ./client
```

Then reference `bakabooru-server` and `bakabooru-client` in your compose file.

## Notes
- Server internal container port is `6666` (`ASPNETCORE_URLS=http://+:6666`).
- Client uses `BACKEND_HOST` and `BACKEND_PORT` to point Nginx to the API container.
- In `docker-compose.example.yml`, server is intentionally not published to host by default; the client talks to it over the compose network.

## Documentation
- Architecture: `docs/architecture.md`
- Agent notes: `agents/`
