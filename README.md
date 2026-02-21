# Damebooru

Damebooru is a self-hosted booru board.

Most booru boards manage their own file storage, not allowing users to use an existing file folder structure. This board is designed to use external libraries similar to Immich. Additionally, the job system was inspired by Immich as well. That includes scheduled library scanning, thumbnail generation etc.

The functionality is relatively basic compared to most booru boards, as I want to use the board as a single user just to manage my own media collection.

At the moment, the project is half-baked, and will very likely stay like that.

**Note:** This project is NOT intended to be used by anyone else other than me. It is 100% tailored for my own use case, and I have no plans to maintain it, fix issues, or add features. Additionally, it is a personal project that I use to test agentic coding, so the code quality might be questionable, at best. Screaming at LLMs in caps didn't seem to improve the results.

## Tech Stack
- ASP.NET Core backend (`server/`) + EF Core with SQLite
- Angular frontend (`client/`)

## Prerequisites (Local Development)
- .NET 10 SDK
- Node.js 22+ and npm
- FFmpeg + FFprobe on `PATH` (required for thumbnails/metadata/similarity jobs)

## Configuration
Before first run, verify these settings in `server/Damebooru.Server/appsettings.json`:
- `ConnectionStrings:DefaultConnection`
- `Damebooru:Storage:DatabasePath`
- `Damebooru:Storage:ThumbnailPath`
- `Damebooru:Storage:TempPath`

Notes:
- Relative storage paths are resolved from `server/Damebooru.Server`.
- Scheduler behavior is controlled by `Damebooru:Processing:RunScheduler`.

## Run Locally

Backend:

```bash
cd server
dotnet run --project Damebooru.Server
```

Frontend:

```bash
cd client
npm install
npm start
```

### Database Migrations
Migrations are auto-applied when `Damebooru.Server` starts.

## Deploy with Docker

### Option A: Use Published Images from GHCR
1. Copy the example compose file:

```bash
cp docker-compose.example.yml docker-compose.yml
```

2. Edit values as needed:
- `Damebooru__Auth__Username` / `Damebooru__Auth__Password`
- volume mounts (`./data/server`, `./media`)
- client port mapping (`8080:80`)

3. Start:

```bash
docker compose up -d
```

### Option B: Build Images Locally
1. Copy the dev compose example:

```bash
cp docker-compose.dev.example.yml docker-compose.yml
```

2. Optionally copy defaults and edit them:

```bash
cp .env.example .env
```

3. Build and start:

```bash
docker compose up -d --build
```

## Notes
- Client uses `BACKEND_HOST` and `BACKEND_PORT` to point Nginx to the API container.
- In `docker-compose.example.yml`, server is intentionally not published to host by default; the client talks to it over the compose network.

## Documentation
- Architecture: `docs/architecture.md`

## Images
<img width="1745" height="1245" alt="image" src="https://github.com/user-attachments/assets/4cac6826-a8d5-4f29-a4ba-5096013ddc4c" />
<img width="1748" height="1243" alt="image" src="https://github.com/user-attachments/assets/9598ef89-07bd-4923-996f-fbe305f0031d" />
