import { Validators } from "@angular/forms";
import { catchError, forkJoin, map, Observable, of, switchMap, tap, timer, throwError } from "rxjs";
import {
  AutoTaggingResult,
  ProviderSetting,
  TaggingProvider,
  SettingValue,
  CategorizedTag,
} from "../models";
import { SaucenaoDb, SaucenaoResult } from "../../api/saucenao/models";
import { SaucenaoService } from "../../api/saucenao/saucenao.service";
import { DanbooruService } from "../../api/danbooru/danbooru.service";
import { GelbooruService } from "../../api/gelbooru/gelbooru.service";
import { Safety } from "@app/services/api/damebooru/models";
import { RateLimiterService } from "../../rate-limiting/rate-limiter.service";

export interface SaucenaoSettings {
  apiKey: string;
  minConfidence: number;
  useDanbooru: boolean;
  danbooruUsername: string;
  danbooruApiKey: string;
  useGelbooru: boolean;
  gelbooruUserId: string;
  gelbooruApiKey: string;
  [key: string]: SettingValue;
}

interface BooruMatch {
  id: number;
  similarity: number;
}

interface BestMatches {
  danbooru: BooruMatch | null;
  gelbooru: BooruMatch | null;
  sources: string[];
  maxSimilarity: number;
  safety?: Safety;
}

export class SaucenaoTaggingProvider implements TaggingProvider<SaucenaoSettings> {
  readonly id = "saucenao-provider";
  readonly name = "SauceNAO";
  readonly priority = 10; // Higher priority than mock
  readonly defaultEnabled = true;

  private enabled = true;

  private settings: SaucenaoSettings = {
    apiKey: "",
    minConfidence: 0.85,
    useDanbooru: true,
    danbooruUsername: "",
    danbooruApiKey: "",
    useGelbooru: true,
    gelbooruUserId: "",
    gelbooruApiKey: "",
  };

  private readonly schema: ProviderSetting[] = [
    {
      key: "apiKey",
      label: "SauceNAO API Key",
      type: "text",
      description: "Enter your SauceNAO API key.",
      defaultValue: "",
      validators: [Validators.required],
    },
    {
      key: "minConfidence",
      label: "Minimum Confidence",
      type: "number",
      description:
        "Minimum similarity score (0 to 1). Higher values mean more strict matching.",
      defaultValue: 0.7,
      validators: [Validators.required, Validators.min(0), Validators.max(1)],
    },
    {
      key: "useDanbooru",
      label: "Use Danbooru",
      type: "boolean",
      description: "Fetch tags from Danbooru when available.",
      defaultValue: true,
    },
    {
      key: "danbooruUsername",
      label: "Danbooru Username",
      type: "text",
      description: "Optional: Your Danbooru username for API access.",
      defaultValue: "",
    },
    {
      key: "danbooruApiKey",
      label: "Danbooru API Key",
      type: "text",
      description: "Optional: Your Danbooru API key.",
      defaultValue: "",
    },
    {
      key: "useGelbooru",
      label: "Use Gelbooru",
      type: "boolean",
      description: "Fetch tags from Gelbooru when available.",
      defaultValue: true,
    },
    {
      key: "gelbooruUserId",
      label: "Gelbooru User ID",
      type: "text",
      description: "Optional: Your Gelbooru user ID.",
      defaultValue: "",
    },
    {
      key: "gelbooruApiKey",
      label: "Gelbooru API Key",
      type: "text",
      description: "Optional: Your Gelbooru API key.",
      defaultValue: "",
    },
  ];

  constructor(
    private saucenao: SaucenaoService,
    private danbooru: DanbooruService,
    private gelbooru: GelbooruService,
    private rateLimiter: RateLimiterService,
  ) { }

  getSettingsSchema(): ProviderSetting[] {
    return this.schema;
  }

  getSettings(): SaucenaoSettings {
    return this.settings;
  }

  updateSettings(settings: SaucenaoSettings): void {
    this.settings = { ...this.settings, ...settings };
  }

  isEnabled(): boolean {
    return this.enabled;
  }

  setEnabled(enabled: boolean): void {
    this.enabled = enabled;
  }

  canHandle(file: File): boolean {
    return file.type.startsWith("image/");
  }

  tag(file: File): Observable<AutoTaggingResult> {
    return this.executeTagWithRetry(file, 0);
  }

  /**
   * Executes tagging with retry logic for 429 errors.
   */
  private executeTagWithRetry(file: File, attempt: number): Observable<AutoTaggingResult> {
    const MAX_RETRIES = 3;

    // Just send the request - handle 429 reactively
    return this.saucenao.search(file, this.settings.apiKey, 15, 999).pipe(
      tap((response) => {
        // Report success for backoff management
        if (response && response.header?.status === 0) {
          this.rateLimiter.reportSuccess('saucenao');
        } else if (response?.header?.status === -2) {
          // SauceNAO signals rate limit in response
          this.rateLimiter.reportRateLimitHit('saucenao');
        }
      }),
      switchMap((response) => {
        // Check for rate limit in response header
        if (response?.header?.status === -2) {
          // SauceNAO signals rate limit, retry after delay
          if (attempt < MAX_RETRIES) {
            // Use rate limiter's backoff time so UI matches actual retry
            const status = this.rateLimiter.getStatus('saucenao');
            const delayMs = status.waitMs > 0 ? status.waitMs : 10000;
            console.log(`SauceNAO rate limited (attempt ${attempt + 1}/${MAX_RETRIES}), retrying in ${delayMs / 1000}s`);
            return timer(delayMs).pipe(
              switchMap(() => this.executeTagWithRetry(file, attempt + 1)),
            );
          }
          console.warn('SauceNAO rate limit: max retries exceeded');
          return throwError(() => new Error('Rate limit exceeded after max retries'));
        }

        if (!response || response.header?.status !== 0) {
          return of(this.emptyResult());
        }

        const matches = this.extractBestMatches(response.results || []);
        if (!matches.danbooru && !matches.gelbooru) {
          // No booru matches, but we may still have sources
          if (matches.sources.length > 0) {
            return of({
              provider: this.name,
              providerId: this.id,
              categorizedTags: [],
              sources: matches.sources,
              confidence: matches.maxSimilarity / 100,
            });
          }
          return of(this.emptyResult());
        }

        return this.fetchAndMergeTags(matches);
      }),
      catchError((error) => {
        // Check if it's a rate limit error (HTTP 429)
        if (error?.status === 429) {
          this.rateLimiter.reportRateLimitHit('saucenao');

          if (attempt < MAX_RETRIES) {
            // Use rate limiter's backoff time so UI matches actual retry
            const status = this.rateLimiter.getStatus('saucenao');
            const delayMs = status.waitMs > 0 ? status.waitMs : 10000;
            console.log(`SauceNAO 429 error (attempt ${attempt + 1}/${MAX_RETRIES}), retrying in ${delayMs / 1000}s`);
            return timer(delayMs).pipe(
              switchMap(() => this.executeTagWithRetry(file, attempt + 1)),
            );
          }
          console.warn('SauceNAO 429: max retries exceeded');
          return throwError(() => new Error('Rate limit exceeded after max retries'));
        } else {
          console.error("SauceNAO provider error:", error);
        }
        return throwError(() => error);
      }),
    );
  }

  /**
   * Extracts the best (highest similarity) post ID for each enabled booru.
   */
  private extractBestMatches(results: SaucenaoResult[]): BestMatches {
    const minConfidence = this.settings.minConfidence * 100;
    let bestDanbooru: BooruMatch | null = null;
    let bestGelbooru: BooruMatch | null = null;
    const sourcesSet = new Set<string>();
    let maxSimilarity = 0;

    for (const result of results) {
      const similarity = parseFloat(result.header?.similarity || "0");
      if (similarity < minConfidence) continue;

      if (similarity > maxSimilarity) {
        maxSimilarity = similarity;
      }

      const data = result.data;
      const indexId = result.header?.index_id;

      // Collect sources from all matching results
      if (data?.ext_urls) {
        for (const url of data.ext_urls) {
          sourcesSet.add(url);
        }
      }

      // Check Danbooru
      if (this.settings.useDanbooru) {
        const postId =
          data?.danbooru_id ??
          (indexId === SaucenaoDb.Danbooru ? data?.post_id : null);
        if (postId && (!bestDanbooru || similarity > bestDanbooru.similarity)) {
          bestDanbooru = { id: postId, similarity };
        }
      }

      // Check Gelbooru
      if (this.settings.useGelbooru) {
        const postId =
          data?.gelbooru_id ??
          (indexId === SaucenaoDb.Gelbooru ? data?.post_id : null);
        if (postId && (!bestGelbooru || similarity > bestGelbooru.similarity)) {
          bestGelbooru = { id: postId, similarity };
        }
      }
    }

    return {
      danbooru: bestDanbooru,
      gelbooru: bestGelbooru,
      sources: Array.from(sourcesSet),
      maxSimilarity,
    };
  }

  /**
   * Fetches tags from the matched boorus and merges them into a single result.
   */
  private fetchAndMergeTags(
    matches: BestMatches,
  ): Observable<AutoTaggingResult> {
    const tasks: Observable<{
      tags: CategorizedTag[];
      safety: "safe" | "sketchy" | "unsafe";
    }>[] = [];

    if (matches.danbooru) {
      tasks.push(this.fetchDanbooruData(matches.danbooru.id));
    }

    if (matches.gelbooru) {
      tasks.push(this.fetchGelbooruData(matches.gelbooru.id));
    }

    return forkJoin(tasks).pipe(
      map((results) => {
        const tagArrays = results.map((r) => r.tags);
        // Use the most conservative safety rating from all sources
        const safety = this.mergeSafetyRatings(results.map((r) => r.safety));
        return this.mergeTagResults(
          tagArrays,
          matches.sources,
          matches.maxSimilarity,
          safety,
        );
      }),
    );
  }

  /**
   * Merges multiple safety ratings, returning the most conservative one.
   * Priority: unsafe > sketchy > safe
   */
  private mergeSafetyRatings(
    safeties: ("safe" | "sketchy" | "unsafe")[],
  ): Safety | undefined {
    if (safeties.length === 0) return undefined;
    if (safeties.includes("unsafe")) return "unsafe";
    if (safeties.includes("sketchy")) return "sketchy";
    return "safe";
  }

  /**
   * Fetches both tags and safety from Danbooru in a single request.
   */
  private fetchDanbooruData(
    postId: number,
  ): Observable<{ tags: CategorizedTag[]; safety: Safety }> {
    return this.danbooru.getPost(postId, {
      username: this.settings.danbooruUsername,
      apiKey: this.settings.danbooruApiKey,
    }).pipe(
      tap(() => this.rateLimiter.reportSuccess('danbooru')),
      map((post) => ({
        tags: post ? this.danbooru.getTags(post) : [],
        safety: post ? this.danbooru.getSafety(post) : "safe",
      })),
      catchError((error) => {
        if (error?.status === 429) {
          this.rateLimiter.reportRateLimitHit('danbooru');
        }
        return of({ tags: [], safety: "safe" as const });
      }),
    );
  }

  /**
   * Fetches both tags and safety from Gelbooru in a single request.
   */
  private fetchGelbooruData(
    postId: number,
  ): Observable<{ tags: CategorizedTag[]; safety: Safety }> {
    return this.gelbooru.getPost(postId, {
      userId: this.settings.gelbooruUserId,
      apiKey: this.settings.gelbooruApiKey,
    }).pipe(
      tap(() => this.rateLimiter.reportSuccess('gelbooru')),
      switchMap((post) =>
        post
          ? this.gelbooru
            .getTags(post, {
              userId: this.settings.gelbooruUserId,
              apiKey: this.settings.gelbooruApiKey,
            })
            .pipe(
              map((tags) => ({
                tags,
                safety: this.gelbooru.getSafety(post),
              })),
            )
          : of({ tags: [], safety: "safe" as const }),
      ),
      catchError((error) => {
        if (error?.status === 429) {
          this.rateLimiter.reportRateLimitHit('gelbooru');
        }
        return of({ tags: [], safety: "safe" as const });
      }),
    );
  }

  /**
   * Merges tag arrays from multiple sources, preferring specific categories over 'general'.
   */
  private mergeTagResults(
    tagArrays: CategorizedTag[][],
    sources: string[],
    maxSimilarity: number,
    safety?: Safety,
  ): AutoTaggingResult {
    const tagMap = new Map<string, string>();

    for (const tags of tagArrays) {
      for (const tag of tags) {
        const existing = tagMap.get(tag.name);
        // Prefer specific categories over 'general'
        if (!existing || existing === "general") {
          tagMap.set(tag.name, tag.category || "general");
        }
      }
    }

    if (tagMap.size === 0 && sources.length === 0) {
      return this.emptyResult();
    }

    return {
      provider: this.name,
      providerId: this.id,
      categorizedTags: Array.from(tagMap.entries()).map(([name, category]) => ({
        name,
        category,
      })),
      sources,
      confidence: maxSimilarity / 100,
      safety,
    };
  }

  private emptyResult(): AutoTaggingResult {
    return {
      provider: this.name,
      providerId: this.id,
      categorizedTags: [],
      confidence: 0,
    };
  }
}
