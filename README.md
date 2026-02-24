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
- FFmpeg + FFprobe on `PATH` (required for video thumbnails/metadata)

## Features
- External library scanning (posts are created for images and videos in the given folder)
- Cron jobs - library scanning, thumbnail generation, metadata extraction, and so on
- Grid with all posts using virtual scroll
- Similar and duplicate post detection
- Tags and tag categories
- Single-user optional authentication
- Search syntax (include/exclude tags, types, tag count, sorting, filename, ..)
- Move/zoom post with mouse
- Auto-tag via SauceNAO + Gelbooru/Danbooru (this is kind of.. not sure if it still works, will change later)

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

## Images
<img width="1382" height="608" alt="Screenshot 2026-02-22 165425" src="https://github.com/user-attachments/assets/9b742e92-2c1b-4620-881f-3eef7b2848c2" />

<img width="1726" height="1244" alt="Screenshot 2026-02-22 165550" src="https://github.com/user-attachments/assets/0c7bc68d-de11-47f2-be73-e229ddc2bdb6" />

<img width="1220" height="1012" alt="Screenshot 2026-02-22 170032" src="https://github.com/user-attachments/assets/fa5c0f49-0666-4634-b13c-72f1dc08777e" />

<img width="1153" height="1239" alt="Screenshot 2026-02-22 165924" src="https://github.com/user-attachments/assets/d881c69d-1fb8-4454-9b7b-497865c26b9a" />

<img width="1916" height="1218" alt="Screenshot 2026-02-22 165850" src="https://github.com/user-attachments/assets/d13ff374-3c04-4bd3-b405-a45ad55910ce" />

<img width="1599" height="667" alt="Screenshot 2026-02-22 165726" src="https://github.com/user-attachments/assets/36f09f7c-ad74-47db-9d2f-4292f423dd84" />

<img width="1724" height="832" alt="Screenshot 2026-02-22 165656" src="https://github.com/user-attachments/assets/8dc878e0-3153-430a-8369-1d0994e38805" />

<img width="2531" height="501" alt="Screenshot 2026-02-22 165246" src="https://github.com/user-attachments/assets/6fdc24b1-b47e-4aff-afcc-1c0794a0a66e" />

<img width="1206" height="1235" alt="Screenshot 2026-02-22 170015" src="https://github.com/user-attachments/assets/593dfcdb-8f40-4459-b803-137d8a843544" />
