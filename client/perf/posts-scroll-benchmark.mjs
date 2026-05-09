import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';
import sharp from 'sharp';

const repoClientDir = resolve(dirname(fileURLToPath(import.meta.url)), '..');

const config = {
  baseUrl: envString('PERF_BASE_URL', 'http://localhost:4200'),
  postsPath: envString('PERF_POSTS_PATH', '/posts'),
  query: envString('PERF_QUERY', ''),
  username: envString('PERF_USERNAME', ''),
  password: envString('PERF_PASSWORD', ''),
  storageState: envString('PERF_STORAGE_STATE', ''),
  saveStorageState: envString('PERF_SAVE_STORAGE_STATE', ''),
  browserPath: envString('PERF_BROWSER_PATH', ''),
  headless: envBool('PERF_HEADLESS', false),
  viewportWidth: envNumber('PERF_VIEWPORT_WIDTH', 1920),
  viewportHeight: envNumber('PERF_VIEWPORT_HEIGHT', 1080),
  settleMs: envNumber('PERF_SETTLE_MS', 1800),
  warmupDistance: envNumber('PERF_WARMUP_DISTANCE', 4000),
  scrollDistance: envNumber('PERF_SCROLL_DISTANCE', 18000),
  scrollStepPx: envNumber('PERF_SCROLL_STEP_PX', 420),
  scrollStepDelayMs: envNumber('PERF_SCROLL_STEP_DELAY_MS', 16),
  runs: envNumber('PERF_RUNS', 1),
  trace: envBool('PERF_TRACE', true),
  outputDir: envString('PERF_OUTPUT_DIR', 'perf/results'),
  thumbnailMode: envString('PERF_THUMBNAIL_MODE', 'normal'),
  thumbnailResizePx: envNumber('PERF_THUMBNAIL_RESIZE_PX', 200),
  thumbnailResizeQuality: envNumber('PERF_THUMBNAIL_RESIZE_QUALITY', 82),
  thumbnailCacheDir: envString('PERF_THUMBNAIL_CACHE_DIR', 'perf/thumbnail-cache'),
  tileCssMode: envString('PERF_TILE_CSS_MODE', envBool('PERF_SIMPLIFY_TILE_CSS', false) ? 'simple' : 'normal'),
  imageAssignmentsPerFrame: envOptionalNumber('PERF_IMAGE_ASSIGNMENTS_PER_FRAME'),
  useScheduledImageSrc: envOptionalBool('PERF_USE_SCHEDULED_IMAGE_SRC'),
};

const transparentPng = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAFgwJ/luzP2wAAAABJRU5ErkJggg==',
  'base64',
);

const traceCategories = [
  '-*',
  'devtools.timeline',
  'disabled-by-default-devtools.timeline',
  'disabled-by-default-devtools.timeline.frame',
  'blink',
  'cc',
  'toplevel',
  'loading',
  'latencyInfo',
].join(',');

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});

async function main() {
  await assertAppAvailable();

  const browser = await chromium.launch({
    executablePath: resolveBrowserPath(),
    headless: config.headless,
    args: [
      '--disable-background-timer-throttling',
      '--disable-renderer-backgrounding',
      '--disable-backgrounding-occluded-windows',
    ],
  });

  const results = [];
  try {
    for (let runIndex = 1; runIndex <= Math.max(1, config.runs); runIndex += 1) {
      console.log(`\nStarting posts scroll benchmark run ${runIndex}/${Math.max(1, config.runs)}...`);
      results.push(await runOnce(browser, runIndex));
    }

    if (results.length > 1) {
      const aggregate = summarizeRuns(results.map((result) => result.summary));
      await saveAggregateResults(aggregate);
      printAggregateSummary(aggregate);
    }
  } finally {
    await browser.close();
  }
}

async function runOnce(browser, runIndex) {
  const contextOptions = {
    viewport: {
      width: config.viewportWidth,
      height: config.viewportHeight,
    },
  };
  if (config.storageState) {
    contextOptions.storageState = resolve(repoClientDir, config.storageState);
  }

  const context = await browser.newContext(contextOptions);
  await configureThumbnailMode(context);
  await configureAppSettings(context);
  const page = await context.newPage();
  const cdp = await context.newCDPSession(page);

  try {
    await page.goto(buildPostsUrl(), { waitUntil: 'domcontentloaded' });
    await waitForPostsOrLogin(page);
    await maybeLogin(page);
    await waitForPostsViewport(page);
    await applyBenchmarkCss(page);
    if (config.saveStorageState) {
      await context.storageState({ path: resolve(repoClientDir, config.saveStorageState) });
    }
    await page.waitForTimeout(config.settleMs);

    await installPageMetrics(page);
    await scrollViewport(page, config.warmupDistance);
    await scrollViewportToTop(page);
    await page.waitForTimeout(config.settleMs);
    await resetPageMetrics(page);

    const tracingComplete = config.trace ? startTracing(cdp) : Promise.resolve(null);
    await scrollViewport(page, config.scrollDistance);
    await page.waitForTimeout(700);
    const trace = config.trace ? await stopTracing(cdp, tracingComplete) : null;
    const pageMetrics = await readPageMetrics(page);
    const summary = summarize(pageMetrics, trace);
    summary.runIndex = runIndex;

    await saveResults(summary, trace);
    printSummary(summary);
    return { summary, trace };
  } finally {
    await context.close();
  }
}

async function configureAppSettings(context) {
  if (config.imageAssignmentsPerFrame === null && config.useScheduledImageSrc === null) {
    return;
  }

  await context.addInitScript(({ imageAssignmentsPerFrame, useScheduledImageSrc }) => {
    const key = 'damebooru_performance_settings';
    const existing = JSON.parse(localStorage.getItem(key) || '{}');
    localStorage.setItem(key, JSON.stringify({
      ...existing,
      ...(imageAssignmentsPerFrame === null ? {} : { scheduledImageAssignmentsPerFrame: imageAssignmentsPerFrame }),
      ...(useScheduledImageSrc === null ? {} : { useScheduledImageSrc }),
    }));
  }, {
    imageAssignmentsPerFrame: config.imageAssignmentsPerFrame,
    useScheduledImageSrc: config.useScheduledImageSrc,
  });
}

async function configureThumbnailMode(context) {
  if (config.thumbnailMode === 'normal') {
    return;
  }

  await context.route('**/thumbnails/**', async (route) => {
    if (config.thumbnailMode === 'block') {
      await route.abort('blockedbyclient');
      return;
    }

    if (config.thumbnailMode === 'tiny') {
      await route.fulfill({
        status: 200,
        contentType: 'image/png',
        body: transparentPng,
      });
      return;
    }

    if (config.thumbnailMode === 'resized') {
      await fulfillWithResizedThumbnail(route);
      return;
    }

    throw new Error(`Unknown PERF_THUMBNAIL_MODE: ${config.thumbnailMode}`);
  });
}

async function fulfillWithResizedThumbnail(route) {
  const cachePath = getResizedThumbnailCachePath(route.request().url());
  if (existsSync(cachePath)) {
    await route.fulfill({
      status: 200,
      contentType: 'image/webp',
      body: await readFile(cachePath),
    });
    return;
  }

  const response = await route.fetch();
  if (!response.ok()) {
    await route.fulfill({ response });
    return;
  }

  const source = await response.body();
  const resized = await sharp(source)
    .resize({
      width: config.thumbnailResizePx,
      height: config.thumbnailResizePx,
      fit: 'inside',
      withoutEnlargement: true,
    })
    .webp({ quality: config.thumbnailResizeQuality })
    .toBuffer();

  await mkdir(dirname(cachePath), { recursive: true });
  await writeFile(cachePath, resized);
  await route.fulfill({
    status: 200,
    contentType: 'image/webp',
    body: resized,
  });
}

function getResizedThumbnailCachePath(url) {
  const size = Math.max(1, Math.floor(config.thumbnailResizePx));
  const quality = Math.max(1, Math.min(100, Math.floor(config.thumbnailResizeQuality)));
  const hash = createHash('sha256').update(url).digest('hex');
  return resolve(
    repoClientDir,
    config.thumbnailCacheDir,
    `${size}px-q${quality}`,
    `${hash}.webp`,
  );
}

async function applyBenchmarkCss(page) {
  const css = getTileCssOverride();
  if (!css) {
    return;
  }

  await page.addStyleTag({ content: css });
}

function getTileCssOverride() {
  switch (config.tileCssMode) {
    case 'normal':
      return '';
    case 'border':
      return `
        .post-tile {
          box-sizing: border-box !important;
          border: 1px solid rgb(255 255 255 / 0.08) !important;
        }
      `;
    case 'radius-md':
      return `
        .post-tile {
          border-radius: 0.375rem !important;
        }
      `;
    case 'radius-xl':
      return `
        .post-tile {
          border-radius: 0.75rem !important;
        }
      `;
    case 'radius-md-border':
      return `
        .post-tile {
          box-sizing: border-box !important;
          border: 1px solid rgb(255 255 255 / 0.08) !important;
          border-radius: 0.375rem !important;
        }
      `;
    case 'radius-xl-border':
      return `
        .post-tile {
          box-sizing: border-box !important;
          border: 1px solid rgb(255 255 255 / 0.08) !important;
          border-radius: 0.75rem !important;
        }
      `;
    case 'hover':
      return `
        .post-tile {
          transition: transform 200ms, box-shadow 200ms, border-color 200ms !important;
        }

        .post-tile:hover {
          z-index: 2 !important;
          transform: translateY(-1px) scale(1.012) !important;
          box-shadow: 0 10px 22px rgb(15 23 42 / 0.36) !important;
        }
      `;
    case 'hover-outline':
      return `
        .post-tile:hover {
          outline: 1px solid rgb(56 189 248 / 0.65) !important;
          outline-offset: -1px !important;
        }
      `;
    case 'hover-border-color':
      return `
        .post-tile {
          box-sizing: border-box !important;
          border: 1px solid rgb(255 255 255 / 0.08) !important;
        }

        .post-tile:hover {
          border-color: rgb(56 189 248 / 0.65) !important;
        }
      `;
    case 'hover-number':
      return `
        .post-tile::after {
          content: "#" attr(data-post-id);
          pointer-events: none;
          position: absolute;
          left: 0.45rem;
          bottom: 0.45rem;
          z-index: 4;
          border-radius: 0.25rem;
          background: rgb(15 23 42 / 0.82);
          color: rgb(248 250 252 / 0.92);
          font: 700 0.72rem/1 system-ui, sans-serif;
          padding: 0.22rem 0.34rem;
          opacity: 0;
          transition: opacity 120ms;
        }

        .post-tile:hover::after {
          opacity: 1;
        }
      `;
    case 'hover-number-no-transition':
      return `
        .post-tile::after {
          content: "#" attr(data-post-id);
          pointer-events: none;
          position: absolute;
          left: 0.45rem;
          bottom: 0.45rem;
          z-index: 4;
          border-radius: 0.25rem;
          background: rgb(15 23 42 / 0.82);
          color: rgb(248 250 252 / 0.92);
          font: 700 0.72rem/1 system-ui, sans-serif;
          padding: 0.22rem 0.34rem;
          opacity: 0;
        }

        .post-tile:hover::after {
          opacity: 1;
        }
      `;
    case 'overlay':
      return `
        .post-tile::after {
          content: "";
          pointer-events: none;
          position: absolute;
          left: 0;
          right: 0;
          bottom: 0;
          z-index: 4;
          min-height: 2.35rem;
          background: linear-gradient(to top, rgb(0 0 0 / 0.82), transparent);
          opacity: 0;
          transition: opacity 150ms;
        }

        .post-tile:hover::after {
          opacity: 1;
        }
      `;
    case 'old-style':
      return `
        .post-tile {
          box-sizing: border-box !important;
          border: 1px solid rgb(148 163 184 / 0.12) !important;
          border-radius: 0.75rem !important;
          transition: transform 200ms, box-shadow 200ms, border-color 200ms !important;
        }

        .post-tile:hover {
          z-index: 2 !important;
          transform: translateY(-1px) scale(1.012) !important;
          border-color: rgb(56 189 248 / 0.5) !important;
          box-shadow: 0 10px 22px rgb(15 23 42 / 0.36) !important;
        }

        .post-tile::after {
          content: "";
          pointer-events: none;
          position: absolute;
          left: 0;
          right: 0;
          bottom: 0;
          z-index: 4;
          min-height: 2.35rem;
          background: linear-gradient(to top, rgb(0 0 0 / 0.82), transparent);
          opacity: 0;
          transition: opacity 150ms;
        }

        .post-tile:hover::after {
          opacity: 1;
        }
      `;
    case 'no-radius':
      return `
        .post-tile {
          border-radius: 0 !important;
        }

        .post-tile img {
          border-radius: 0 !important;
        }
      `;
    case 'no-overlay':
      return `
        .post-overlay {
          display: none !important;
        }
      `;
    case 'no-hover':
      return `
        .post-tile {
          transition: none !important;
        }

        .post-tile:hover {
          transform: none !important;
          box-shadow: none !important;
        }

        .post-overlay {
          transition: none !important;
        }
      `;
    case 'simple':
      return `
      .post-tile {
        border-radius: 0 !important;
        border-color: transparent !important;
        box-shadow: none !important;
        transition: none !important;
      }

      .post-tile:hover {
        transform: none !important;
        box-shadow: none !important;
      }

      .post-overlay {
        display: none !important;
      }
    `;
    default:
      throw new Error(`Unknown PERF_TILE_CSS_MODE: ${config.tileCssMode}`);
  }
}

async function assertAppAvailable() {
  try {
    const response = await fetch(config.baseUrl);
    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`);
    }
  } catch (error) {
    throw new Error(
      `Could not reach ${config.baseUrl}. Start the client dev server first, or set PERF_BASE_URL. ${String(error)}`,
    );
  }
}

function buildPostsUrl() {
  const url = new URL(config.postsPath, config.baseUrl);
  if (config.query) {
    url.searchParams.set('query', config.query);
  }
  return url.toString();
}

async function maybeLogin(page) {
  if (!isLoginUrl(page.url())) {
    return;
  }

  if (!config.username || !config.password) {
    throw new Error(
      'The benchmark reached /login. Set PERF_USERNAME and PERF_PASSWORD, or provide PERF_STORAGE_STATE.',
    );
  }

  await page.fill('#username', config.username);
  await page.fill('#password', config.password);
  await Promise.all([
    page.waitForURL((url) => !url.pathname.includes('/login'), { timeout: 15000 }),
    page.click('button[type="submit"]'),
  ]);
  await waitForPostsOrLogin(page);
}

async function waitForPostsOrLogin(page) {
  if (isLoginUrl(page.url())) {
    return;
  }

  if (await page.locator('.posts-virtual-viewport').first().isVisible().catch(() => false)) {
    return;
  }

  await Promise.race([
    page.waitForSelector('.posts-virtual-viewport', { state: 'visible', timeout: 30000 }),
    page.waitForURL((url) => isLoginUrl(url.toString()), { timeout: 30000 }),
  ]);
}

async function waitForPostsViewport(page) {
  try {
    await page.waitForSelector('.posts-virtual-viewport', { state: 'visible', timeout: 30000 });
  } catch (error) {
    throw new Error(
      `Posts viewport was not found. Current URL: ${page.url()}. Title: ${await page.title()}. ${String(error)}`,
    );
  }
  await page.waitForFunction(() => {
    const viewport = document.querySelector('.posts-virtual-viewport');
    return viewport && viewport.scrollHeight > viewport.clientHeight;
  }, { timeout: 30000 });
}

function isLoginUrl(url) {
  return new URL(url).pathname.includes('/login');
}

async function installPageMetrics(page) {
  await page.evaluate(() => {
    const viewport = document.querySelector('.posts-virtual-viewport');
    if (!viewport) {
      throw new Error('Posts virtual viewport was not found.');
    }

    window.__postsScrollPerf?.dispose?.();

    const state = {
      frameDeltas: [],
      longTasks: [],
      scrollSamples: [],
      renderedTileSamples: [],
      imagePendingSamples: [],
      scrollEvents: 0,
      startTime: performance.now(),
      lastFrameTime: null,
      rafId: 0,
      observer: null,
      scrollListener: null,
      sampleEveryMs: 100,
      lastSampleTime: 0,
    };

    const sample = (time) => {
      state.renderedTileSamples.push(document.querySelectorAll('.post-tile').length);
      state.imagePendingSamples.push(document.querySelectorAll('[data-scheduled-pending="true"]').length);
      state.scrollSamples.push({
        time: time - state.startTime,
        top: viewport.scrollTop,
      });
    };

    const frame = (time) => {
      if (state.lastFrameTime !== null) {
        state.frameDeltas.push(time - state.lastFrameTime);
      }
      state.lastFrameTime = time;

      if (time - state.lastSampleTime >= state.sampleEveryMs) {
        state.lastSampleTime = time;
        sample(time);
      }

      state.rafId = requestAnimationFrame(frame);
    };

    state.scrollListener = () => {
      state.scrollEvents += 1;
    };
    viewport.addEventListener('scroll', state.scrollListener, { passive: true });

    if ('PerformanceObserver' in window) {
      try {
        state.observer = new PerformanceObserver((list) => {
          for (const entry of list.getEntries()) {
            state.longTasks.push({
              startTime: entry.startTime,
              duration: entry.duration,
              name: entry.name,
            });
          }
        });
        state.observer.observe({ entryTypes: ['longtask'] });
      } catch {
        state.observer = null;
      }
    }

    state.rafId = requestAnimationFrame(frame);

    window.__postsScrollPerf = {
      reset() {
        state.frameDeltas.length = 0;
        state.longTasks.length = 0;
        state.scrollSamples.length = 0;
        state.renderedTileSamples.length = 0;
        state.imagePendingSamples.length = 0;
        state.scrollEvents = 0;
        state.startTime = performance.now();
        state.lastFrameTime = null;
        state.lastSampleTime = 0;
      },
      read() {
        return {
          frameDeltas: [...state.frameDeltas],
          longTasks: [...state.longTasks],
          scrollSamples: [...state.scrollSamples],
          renderedTileSamples: [...state.renderedTileSamples],
          imagePendingSamples: [...state.imagePendingSamples],
          scrollEvents: state.scrollEvents,
          durationMs: performance.now() - state.startTime,
          finalScrollTop: viewport.scrollTop,
          maxScrollTop: viewport.scrollHeight - viewport.clientHeight,
          viewportHeight: viewport.clientHeight,
          scrollHeight: viewport.scrollHeight,
        };
      },
      dispose() {
        cancelAnimationFrame(state.rafId);
        viewport.removeEventListener('scroll', state.scrollListener);
        state.observer?.disconnect?.();
      },
    };
  });
}

async function resetPageMetrics(page) {
  await page.evaluate(() => window.__postsScrollPerf.reset());
}

async function readPageMetrics(page) {
  return await page.evaluate(() => window.__postsScrollPerf.read());
}

async function scrollViewport(page, distance) {
  const steps = Math.max(1, Math.ceil(distance / config.scrollStepPx));
  const step = distance / steps;

  await dismissTransientUi(page);
  await moveMouseToViewportCenter(page);
  for (let i = 0; i < steps; i += 1) {
    await page.mouse.wheel(0, step);
    await page.waitForTimeout(config.scrollStepDelayMs);
  }
}

async function dismissTransientUi(page) {
  await page.keyboard.press('Escape').catch(() => {});
  await page.waitForTimeout(50);
}

async function moveMouseToViewportCenter(page) {
  const point = await page.evaluate(() => {
    const viewport = document.querySelector('.posts-virtual-viewport');
    if (!viewport) {
      throw new Error('Posts virtual viewport was not found.');
    }

    const rect = viewport.getBoundingClientRect();
    return {
      x: rect.left + rect.width / 2,
      y: rect.top + Math.min(rect.height / 2, 220),
    };
  });

  await page.mouse.move(point.x, point.y);
}

async function scrollViewportToTop(page) {
  await page.evaluate(() => {
    const viewport = document.querySelector('.posts-virtual-viewport');
    if (viewport) {
      viewport.scrollTop = 0;
    }
  });
}

async function startTracing(cdp) {
  await cdp.send('Tracing.start', {
    categories: traceCategories,
    transferMode: 'ReturnAsStream',
  });

  return new Promise((resolve) => {
    cdp.once('Tracing.tracingComplete', resolve);
  });
}

async function stopTracing(cdp, tracingComplete) {
  await cdp.send('Tracing.end');
  const event = await tracingComplete;
  return await readTraceStream(cdp, event.stream);
}

async function readTraceStream(cdp, stream) {
  let trace = '';
  while (true) {
    const chunk = await cdp.send('IO.read', { handle: stream });
    trace += chunk.data ?? '';
    if (chunk.eof) {
      break;
    }
  }
  await cdp.send('IO.close', { handle: stream });
  return JSON.parse(trace);
}

function summarize(pageMetrics, trace) {
  const frames = summarizeFrames(pageMetrics.frameDeltas);
  const traceSummary = trace ? summarizeTrace(trace.traceEvents ?? []) : null;

  return {
    timestamp: new Date().toISOString(),
    config: {
      baseUrl: config.baseUrl,
      postsPath: config.postsPath,
      query: config.query,
      viewport: `${config.viewportWidth}x${config.viewportHeight}`,
      headless: config.headless,
      scrollDistance: config.scrollDistance,
      scrollStepPx: config.scrollStepPx,
      scrollStepDelayMs: config.scrollStepDelayMs,
      runs: config.runs,
      trace: config.trace,
      thumbnailMode: config.thumbnailMode,
      thumbnailResizePx: config.thumbnailResizePx,
      thumbnailResizeQuality: config.thumbnailResizeQuality,
      tileCssMode: config.tileCssMode,
      imageAssignmentsPerFrame: config.imageAssignmentsPerFrame,
      useScheduledImageSrc: config.useScheduledImageSrc,
    },
    page: {
      durationMs: round(pageMetrics.durationMs),
      finalScrollTop: round(pageMetrics.finalScrollTop),
      maxScrollTop: round(pageMetrics.maxScrollTop),
      viewportHeight: round(pageMetrics.viewportHeight),
      scrollHeight: round(pageMetrics.scrollHeight),
      scrollEvents: pageMetrics.scrollEvents,
      renderedTiles: summarizeSamples(pageMetrics.renderedTileSamples),
      pendingImages: summarizeSamples(pageMetrics.imagePendingSamples),
      longTasks: summarizeLongTasks(pageMetrics.longTasks),
      frames,
    },
    trace: traceSummary,
  };
}

function summarizeRuns(summaries) {
  const values = {
    frameP95Ms: summaries.map((summary) => summary.page.frames.p95Ms),
    frameP99Ms: summaries.map((summary) => summary.page.frames.p99Ms),
    frameMaxMs: summaries.map((summary) => summary.page.frames.maxMs),
    over4_2Pct: summaries.map((summary) => summary.page.frames.over4_2Pct),
    over8_3Pct: summaries.map((summary) => summary.page.frames.over8_3Pct),
    over16_7Pct: summaries.map((summary) => summary.page.frames.over16_7Pct),
    over16_7ms: summaries.map((summary) => summary.page.frames.over16_7ms),
    over33_3ms: summaries.map((summary) => summary.page.frames.over33_3ms),
    renderedTilesAvg: summaries.map((summary) => summary.page.renderedTiles.avg),
    pendingImagesAvg: summaries.map((summary) => summary.page.pendingImages.avg),
    longTaskCount: summaries.map((summary) => summary.page.longTasks.count),
    scriptingMs: summaries.map((summary) => summary.trace?.scriptingMs ?? 0),
    layoutMs: summaries.map((summary) => summary.trace?.layoutMs ?? 0),
    paintMs: summaries.map((summary) => summary.trace?.paintMs ?? 0),
    rasterMs: summaries.map((summary) => summary.trace?.rasterMs ?? 0),
    compositeMs: summaries.map((summary) => summary.trace?.compositeMs ?? 0),
    imageDecodeMs: summaries.map((summary) => summary.trace?.imageDecodeMs ?? 0),
  };

  return {
    timestamp: new Date().toISOString(),
    runs: summaries.length,
    config: summaries[0]?.config ?? {},
    metrics: Object.fromEntries(
      Object.entries(values).map(([name, metricValues]) => [name, summarizeMetric(metricValues)]),
    ),
    runOutputs: summaries.map((summary) => summary.output),
  };
}

function summarizeMetric(values) {
  const sorted = numericSort(values);
  return {
    median: percentile(sorted, 50),
    min: round(sorted[0] ?? 0),
    max: round(sorted.at(-1) ?? 0),
    avg: round(avg(sorted)),
  };
}

function summarizeFrames(frameDeltas) {
  const sorted = numericSort(frameDeltas);
  return {
    count: sorted.length,
    avgMs: round(avg(sorted)),
    p50Ms: percentile(sorted, 50),
    p95Ms: percentile(sorted, 95),
    p99Ms: percentile(sorted, 99),
    maxMs: round(sorted.at(-1) ?? 0),
    over2_1ms: sorted.filter((value) => value > 2.08).length,
    over4_2ms: sorted.filter((value) => value > 4.17).length,
    over8_3ms: sorted.filter((value) => value > 8.33).length,
    over16_7ms: sorted.filter((value) => value > 16.67).length,
    over33_3ms: sorted.filter((value) => value > 33.33).length,
    over50ms: sorted.filter((value) => value > 50).length,
    over2_1Pct: percent(sorted.filter((value) => value > 2.08).length, sorted.length),
    over4_2Pct: percent(sorted.filter((value) => value > 4.17).length, sorted.length),
    over8_3Pct: percent(sorted.filter((value) => value > 8.33).length, sorted.length),
    over16_7Pct: percent(sorted.filter((value) => value > 16.67).length, sorted.length),
  };
}

function summarizeLongTasks(longTasks) {
  const durations = numericSort(longTasks.map((task) => task.duration));
  return {
    count: durations.length,
    totalMs: round(sum(durations)),
    p95Ms: percentile(durations, 95),
    maxMs: round(durations.at(-1) ?? 0),
  };
}

function summarizeSamples(samples) {
  const sorted = numericSort(samples);
  return {
    min: round(sorted[0] ?? 0),
    avg: round(avg(sorted)),
    max: round(sorted.at(-1) ?? 0),
  };
}

function summarizeTrace(events) {
  const buckets = {
    scriptingMs: 0,
    layoutMs: 0,
    paintMs: 0,
    rasterMs: 0,
    compositeMs: 0,
    imageDecodeMs: 0,
    unclassifiedTimelineMs: 0,
  };
  const eventStats = new Map();

  for (const event of events) {
    if (event.ph !== 'X' || typeof event.dur !== 'number') {
      continue;
    }

    const ms = event.dur / 1000;
    const stats = eventStats.get(event.name) ?? { name: event.name, count: 0, totalMs: 0, maxMs: 0 };
    stats.count += 1;
    stats.totalMs += ms;
    stats.maxMs = Math.max(stats.maxMs, ms);
    eventStats.set(event.name, stats);

    if (isScriptingEvent(event.name)) {
      buckets.scriptingMs += ms;
    } else if (isLayoutEvent(event.name)) {
      buckets.layoutMs += ms;
    } else if (isPaintEvent(event.name)) {
      buckets.paintMs += ms;
    } else if (isRasterEvent(event.name)) {
      buckets.rasterMs += ms;
    } else if (isCompositeEvent(event.name)) {
      buckets.compositeMs += ms;
    } else if (isImageDecodeEvent(event.name)) {
      buckets.imageDecodeMs += ms;
    } else if (event.cat?.includes('devtools.timeline')) {
      buckets.unclassifiedTimelineMs += ms;
    }
  }

  const topEventsByDuration = Array.from(eventStats.values())
    .filter((stats) => !isGenericTaskWrapper(stats.name))
    .sort((left, right) => right.totalMs - left.totalMs)
    .slice(0, 12)
    .map((stats) => ({
      name: stats.name,
      totalMs: round(stats.totalMs),
      count: stats.count,
      avgMs: round(stats.totalMs / stats.count),
      maxMs: round(stats.maxMs),
    }));

  const topEventCounts = Array.from(eventStats.values())
    .sort((left, right) => right.count - left.count)
    .slice(0, 8)
    .map((stats) => ({
      name: stats.name,
      count: stats.count,
    }));

  const bucketDurations = Object.entries(buckets)
    .map(([name, totalMs]) => ({ name, totalMs: round(totalMs) }))
    .sort((left, right) => right.totalMs - left.totalMs);

  return {
    ...Object.fromEntries(Object.entries(buckets).map(([key, value]) => [key, round(value)])),
    topBuckets: bucketDurations,
    topEventsByDuration,
    topEventCounts,
  };
}

function isScriptingEvent(name) {
  return [
    'FunctionCall',
    'EvaluateScript',
    'EventDispatch',
    'FireAnimationFrame',
    'TimerFire',
    'v8.compile',
  ].includes(name);
}

function isLayoutEvent(name) {
  return [
    'Layout',
    'UpdateLayoutTree',
    'RecalculateStyles',
    'PrePaint',
  ].includes(name);
}

function isPaintEvent(name) {
  return [
    'Paint',
    'PaintImage',
    'Layerize',
  ].includes(name);
}

function isRasterEvent(name) {
  return name.includes('Raster') || name === 'GPUTask';
}

function isCompositeEvent(name) {
  return [
    'CompositeLayers',
    'Commit',
    'ActivateLayerTree',
    'DrawFrame',
  ].includes(name);
}

function isImageDecodeEvent(name) {
  return name.includes('Decode') || name.includes('ImageDecode');
}

function isGenericTaskWrapper(name) {
  return [
    'RunTask',
    'ThreadControllerImpl::RunTask',
    'ThreadPool_RunTask',
    'Receive mojo message',
    'SimpleWatcher::OnHandleReady',
    'IOHandler::OnIOCompleted',
    'Closed mojo endpoint',
  ].includes(name);
}

async function saveResults(summary, trace) {
  const outputDir = resolve(repoClientDir, config.outputDir);
  await mkdir(outputDir, { recursive: true });
  const stamp = summary.timestamp.replace(/[:.]/g, '-');
  const summaryPath = join(outputDir, `posts-scroll-${stamp}.json`);

  if (trace) {
    const tracePath = join(outputDir, `posts-scroll-${stamp}.trace.json`);
    await writeFile(tracePath, JSON.stringify(trace), 'utf8');
    summary.output = { summaryPath, tracePath };
  } else {
    summary.output = { summaryPath };
  }

  await writeFile(summaryPath, `${JSON.stringify(summary, null, 2)}\n`, 'utf8');
}

async function saveAggregateResults(aggregate) {
  const outputDir = resolve(repoClientDir, config.outputDir);
  await mkdir(outputDir, { recursive: true });
  const stamp = aggregate.timestamp.replace(/[:.]/g, '-');
  const aggregatePath = join(outputDir, `posts-scroll-aggregate-${stamp}.json`);
  aggregate.output = { aggregatePath };
  await writeFile(aggregatePath, `${JSON.stringify(aggregate, null, 2)}\n`, 'utf8');
}

function printSummary(summary) {
  const frames = summary.page.frames;
  const longTasks = summary.page.longTasks;
  const trace = summary.trace;

  console.log('\nPosts Scroll Benchmark');
  if (config.runs > 1) {
    console.log(`Run: ${summary.runIndex}/${config.runs}`);
  }
  console.log(`URL: ${buildPostsUrl()}`);
  console.log(`Viewport: ${summary.config.viewport} (${summary.config.headless ? 'headless' : 'headed'})`);
  console.log(`Variant: thumbnails=${formatThumbnailVariant(summary.config)}, tileCss=${summary.config.tileCssMode}`);
  if (summary.config.imageAssignmentsPerFrame !== null || summary.config.useScheduledImageSrc !== null) {
    console.log(`Settings: assignments/frame=${summary.config.imageAssignmentsPerFrame ?? 'stored'}, scheduledSrc=${summary.config.useScheduledImageSrc ?? 'stored'}`);
  }
  console.log(`Duration: ${summary.page.durationMs}ms`);
  console.log(`Scroll top: ${summary.page.finalScrollTop} / ${summary.page.maxScrollTop}`);
  console.log('');
  console.log('Frames');
  console.log(`  count: ${frames.count}`);
  console.log(`  avg / p50 / p95 / p99 / max: ${frames.avgMs} / ${frames.p50Ms} / ${frames.p95Ms} / ${frames.p99Ms} / ${frames.maxMs} ms`);
  console.log(`  >2.1ms: ${frames.over2_1ms} (${frames.over2_1Pct}%), >4.2ms: ${frames.over4_2ms} (${frames.over4_2Pct}%)`);
  console.log(`  >8.3ms: ${frames.over8_3ms} (${frames.over8_3Pct}%), >16.7ms: ${frames.over16_7ms} (${frames.over16_7Pct}%), >33.3ms: ${frames.over33_3ms}, >50ms: ${frames.over50ms}`);
  console.log('');
  console.log('Page');
  console.log(`  long tasks: ${longTasks.count}, total: ${longTasks.totalMs}ms, p95: ${longTasks.p95Ms}ms, max: ${longTasks.maxMs}ms`);
  console.log(`  rendered tiles avg/max: ${summary.page.renderedTiles.avg} / ${summary.page.renderedTiles.max}`);
  console.log(`  pending images avg/max: ${summary.page.pendingImages.avg} / ${summary.page.pendingImages.max}`);

  if (trace) {
    console.log('');
    console.log('Trace Durations');
    console.log(`  scripting: ${trace.scriptingMs}ms`);
    console.log(`  layout: ${trace.layoutMs}ms`);
    console.log(`  paint: ${trace.paintMs}ms`);
    console.log(`  raster: ${trace.rasterMs}ms`);
    console.log(`  composite: ${trace.compositeMs}ms`);
    console.log(`  image decode: ${trace.imageDecodeMs}ms`);
    console.log(`  unclassified timeline: ${trace.unclassifiedTimelineMs}ms`);
    console.log('  top buckets:');
    for (const bucket of trace.topBuckets.slice(0, 5)) {
      console.log(`    ${bucket.name}: ${bucket.totalMs}ms`);
    }
    console.log('  top events by duration:');
    for (const event of trace.topEventsByDuration.slice(0, 6)) {
      console.log(`    ${event.name}: ${event.totalMs}ms (${event.count}x, max ${event.maxMs}ms)`);
    }
  }

  console.log('');
  console.log(`Saved: ${summary.output.summaryPath}`);
  if (summary.output.tracePath) {
    console.log(`Trace: ${summary.output.tracePath}`);
  }
}

function printAggregateSummary(aggregate) {
  const metric = aggregate.metrics;

  console.log('\nPosts Scroll Benchmark Aggregate');
  console.log(`Runs: ${aggregate.runs}`);
  console.log(`Variant: thumbnails=${formatThumbnailVariant(aggregate.config)}, tileCss=${aggregate.config.tileCssMode}`);
  if (aggregate.config.imageAssignmentsPerFrame !== null || aggregate.config.useScheduledImageSrc !== null) {
    console.log(`Settings: assignments/frame=${aggregate.config.imageAssignmentsPerFrame ?? 'stored'}, scheduledSrc=${aggregate.config.useScheduledImageSrc ?? 'stored'}`);
  }
  console.log('');
  console.log('Frames (median, min..max)');
  printMetricLine('  p95', metric.frameP95Ms, 'ms');
  printMetricLine('  p99', metric.frameP99Ms, 'ms');
  printMetricLine('  max', metric.frameMaxMs, 'ms');
  printMetricLine('  >4.2ms', metric.over4_2Pct, '%');
  printMetricLine('  >8.3ms', metric.over8_3Pct, '%');
  printMetricLine('  >16.7ms', metric.over16_7Pct, '%');
  printMetricLine('  >16.7ms count', metric.over16_7ms, '');
  printMetricLine('  >33.3ms count', metric.over33_3ms, '');
  console.log('');
  console.log('Page (median, min..max)');
  printMetricLine('  rendered tiles avg', metric.renderedTilesAvg, '');
  printMetricLine('  pending images avg', metric.pendingImagesAvg, '');
  printMetricLine('  long tasks', metric.longTaskCount, '');

  if (aggregate.config.trace) {
    console.log('');
    console.log('Trace Durations (median, min..max)');
    printMetricLine('  scripting', metric.scriptingMs, 'ms');
    printMetricLine('  layout', metric.layoutMs, 'ms');
    printMetricLine('  paint', metric.paintMs, 'ms');
    printMetricLine('  raster', metric.rasterMs, 'ms');
    printMetricLine('  composite', metric.compositeMs, 'ms');
    printMetricLine('  image decode', metric.imageDecodeMs, 'ms');
  }

  console.log('');
  console.log(`Saved aggregate: ${aggregate.output.aggregatePath}`);
}

function formatThumbnailVariant(summaryConfig) {
  if (summaryConfig.thumbnailMode !== 'resized') {
    return summaryConfig.thumbnailMode;
  }

  return `resized:${summaryConfig.thumbnailResizePx}px:q${summaryConfig.thumbnailResizeQuality}`;
}

function printMetricLine(label, metric, unit) {
  const suffix = unit ? ` ${unit}` : '';
  console.log(`${label}: ${metric.median}${suffix} (${metric.min}..${metric.max}${suffix}, avg ${metric.avg}${suffix})`);
}

function resolveBrowserPath() {
  if (config.browserPath) {
    return config.browserPath;
  }

  const candidates = [
    process.env.CHROME_PATH,
    process.env.EDGE_PATH,
    'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
    'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
    '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge',
    '/usr/bin/google-chrome',
    '/usr/bin/google-chrome-stable',
    '/usr/bin/chromium',
    '/usr/bin/chromium-browser',
    '/usr/bin/microsoft-edge',
  ].filter(Boolean);

  const match = candidates.find((candidate) => existsSync(candidate));
  if (!match) {
    throw new Error('Could not find Chrome/Edge. Set PERF_BROWSER_PATH to the browser executable.');
  }

  return match;
}

function envString(name, fallback) {
  return process.env[name] ?? fallback;
}

function envNumber(name, fallback) {
  const raw = process.env[name];
  if (!raw) {
    return fallback;
  }

  const parsed = Number(raw);
  return Number.isFinite(parsed) ? parsed : fallback;
}

function envBool(name, fallback) {
  const raw = process.env[name];
  if (!raw) {
    return fallback;
  }

  return raw === '1' || raw.toLowerCase() === 'true';
}

function envOptionalNumber(name) {
  const raw = process.env[name];
  if (!raw) {
    return null;
  }

  const parsed = Number(raw);
  return Number.isFinite(parsed) ? parsed : null;
}

function envOptionalBool(name) {
  const raw = process.env[name];
  if (!raw) {
    return null;
  }

  return raw === '1' || raw.toLowerCase() === 'true';
}

function numericSort(values) {
  return values
    .filter((value) => Number.isFinite(value))
    .sort((left, right) => left - right);
}

function percentile(sortedValues, percentileValue) {
  if (sortedValues.length === 0) {
    return 0;
  }

  const index = Math.min(
    sortedValues.length - 1,
    Math.max(0, Math.ceil(sortedValues.length * percentileValue / 100) - 1),
  );
  return round(sortedValues[index]);
}

function avg(values) {
  return values.length === 0 ? 0 : sum(values) / values.length;
}

function percent(count, total) {
  return total <= 0 ? 0 : round(count / total * 100);
}

function sum(values) {
  return values.reduce((total, value) => total + value, 0);
}

function round(value) {
  return Math.round(value * 100) / 100;
}
