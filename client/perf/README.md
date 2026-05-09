# Posts Scroll Benchmark

Runs an automated Chrome/Edge scroll benchmark against the real posts page.

```powershell
pnpm run perf:posts
```

The client dev server should already be running, usually at `http://localhost:4200`.
The server/API can be your normal local server and real library.

## Authentication

If auth is enabled, either provide credentials:

```powershell
$env:PERF_USERNAME='your-user'
$env:PERF_PASSWORD='your-password'
pnpm run perf:posts
```

Or save a reusable storage state once:

```powershell
$env:PERF_USERNAME='your-user'
$env:PERF_PASSWORD='your-password'
$env:PERF_SAVE_STORAGE_STATE='perf/auth-state.json'
pnpm run perf:posts
```

Then future runs can use:

```powershell
$env:PERF_STORAGE_STATE='perf/auth-state.json'
pnpm run perf:posts
```

## Useful Options

- `PERF_BASE_URL`: app URL. Default: `http://localhost:4200`
- `PERF_POSTS_PATH`: posts route. Default: `/posts`
- `PERF_QUERY`: optional fixed posts query
- `PERF_BROWSER_PATH`: Chrome/Edge executable path if auto-detection fails
- `PERF_HEADLESS=1`: run headless. Headed mode is the default because it is closer to visible scroll behavior.
- `PERF_VIEWPORT_WIDTH` / `PERF_VIEWPORT_HEIGHT`: browser viewport. Default: `1920x1080`
- `PERF_SCROLL_DISTANCE`: measured scroll distance in px. Default: `18000`
- `PERF_SCROLL_STEP_PX`: wheel delta per step. Default: `420`
- `PERF_SCROLL_STEP_DELAY_MS`: delay between wheel steps. Default: `16`
- `PERF_RUNS`: repeat the full benchmark and write an aggregate median/min/max report. Default: `1`
- `PERF_TRACE=0`: skip Chrome trace capture and collect only in-page metrics
- `PERF_OUTPUT_DIR`: output folder. Default: `perf/results`
- `PERF_THUMBNAIL_MODE`: `normal`, `resized`, `tiny`, or `block`. Default: `normal`
- `PERF_THUMBNAIL_RESIZE_PX`: max width/height for `resized` thumbnails. Default: `200`
- `PERF_THUMBNAIL_RESIZE_QUALITY`: WebP quality for `resized` thumbnails. Default: `82`
- `PERF_THUMBNAIL_CACHE_DIR`: cache folder for generated benchmark thumbnails. Default: `perf/thumbnail-cache`
- `PERF_TILE_CSS_MODE`: `normal`, `border`, `radius-md`, `radius-xl`, `radius-md-border`, `radius-xl-border`, `hover`, `hover-outline`, `hover-border-color`, `hover-number`, `hover-number-no-transition`, `overlay`, `old-style`, `no-radius`, `no-overlay`, `no-hover`, or `simple`. Default: `normal`
- `PERF_SIMPLIFY_TILE_CSS=1`: legacy shortcut for `PERF_TILE_CSS_MODE=simple`
- `PERF_IMAGE_ASSIGNMENTS_PER_FRAME`: override the scheduled image assignment setting for the benchmark
- `PERF_USE_SCHEDULED_IMAGE_SRC`: `1` or `0`, overrides scheduled image loading for the benchmark

## Output

The script prints a compact report and writes:

- `posts-scroll-*.json`: summary metrics
- `posts-scroll-*.trace.json`: Chrome trace, when `PERF_TRACE` is enabled

The summary includes frame deltas from `requestAnimationFrame`, long tasks, rendered tile samples, pending image samples, and coarse Chrome trace buckets for scripting, layout, paint, raster, composite, image decode, and unclassified timeline work. Frame thresholds include 480Hz-ish (`>2.1ms`), 240Hz-ish (`>4.2ms`), 120Hz-ish (`>8.3ms`), and 60Hz-ish (`>16.7ms`) budgets. Trace events are ranked by total duration first; event counts are kept only as secondary context.

## Thumbnail Variants

`PERF_THUMBNAIL_MODE=tiny` replaces every thumbnail with a 1x1 transparent PNG. It is only a diagnostic upper bound for "what if image work almost disappeared?"

`PERF_THUMBNAIL_MODE=resized` keeps the real thumbnail content but rewrites thumbnail responses through a local disk cache at a smaller size:

```powershell
$env:PERF_THUMBNAIL_MODE='resized'
$env:PERF_THUMBNAIL_RESIZE_PX='200'
pnpm run perf:posts
```

The first run may spend CPU generating `perf/thumbnail-cache`; run the same command again when comparing results so the measurement is mostly browser rendering work, not Node resizing work.
