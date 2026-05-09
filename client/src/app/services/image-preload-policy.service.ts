import { Injectable, inject } from '@angular/core';

import { SettingsService, type ImagePreloadMode } from './settings.service';

export interface ImagePreloadPlan {
  enabled: boolean;
  concurrency: number;
  acceptedUrlCount: number;
}

export interface ImagePreloadResult {
  durationMs: number;
  loaded: boolean;
}

interface NetworkInformationLike {
  saveData?: boolean;
}

interface ImagePreloadLimits {
  concurrency: number;
  acceptedUrlCount: number;
}

@Injectable({
  providedIn: 'root',
})
export class ImagePreloadPolicyService {
  private readonly settingsService = inject(SettingsService);
  private readonly cacheHitThresholdMs = 5;
  private readonly sampleLimit = 12;
  private readonly adaptiveWarmupLimits: ImagePreloadLimits = { concurrency: 2, acceptedUrlCount: 8 };
  private readonly adaptiveRunwayMs = 1000;
  private readonly adaptiveConcurrencyTargetMs = 500;
  private readonly adaptiveMinAcceptedUrlCount = 2;
  private readonly adaptiveMaxAcceptedUrlCount = 24;
  private readonly adaptiveMaxConcurrency = 4;
  private readonly samples: number[] = [];

  plan(requestedUrlCount: number, requestedConcurrency: number): ImagePreloadPlan {
    const mode = this.settingsService.performanceSettings().imagePreloadMode;
    if (mode === 'off' || this.saveDataEnabled()) {
      return { enabled: false, concurrency: 0, acceptedUrlCount: 0 };
    }

    const limits = this.resolveLimits(mode, requestedConcurrency);
    return {
      enabled: requestedUrlCount > 0 && limits.acceptedUrlCount > 0,
      concurrency: limits.concurrency,
      acceptedUrlCount: Math.min(requestedUrlCount, limits.acceptedUrlCount),
    };
  }

  recordLoad(result: ImagePreloadResult): void {
    if (!result.loaded || result.durationMs < this.cacheHitThresholdMs) {
      return;
    }

    this.samples.push(result.durationMs);
    if (this.samples.length > this.sampleLimit) {
      this.samples.shift();
    }
  }

  private resolveLimits(
    mode: ImagePreloadMode,
    requestedConcurrency: number,
  ): ImagePreloadLimits {
    const maxConcurrency = Math.max(1, Math.floor(requestedConcurrency));

    switch (mode) {
      case 'conservative':
        return { concurrency: 1, acceptedUrlCount: 6 };
      case 'aggressive':
        return { concurrency: Math.min(maxConcurrency, 4), acceptedUrlCount: 32 };
      case 'adaptive':
      default:
        return this.resolveAdaptiveLimits(maxConcurrency);
    }
  }

  private resolveAdaptiveLimits(maxConcurrency: number): ImagePreloadLimits {
    if (this.samples.length < 4) {
      return this.capConcurrency(this.adaptiveWarmupLimits, maxConcurrency);
    }

    const estimatedLoadMs = this.estimateLoadMs();
    const concurrency = this.resolveAdaptiveConcurrency(estimatedLoadMs, maxConcurrency);
    return {
      concurrency,
      acceptedUrlCount: this.resolveAdaptiveUrlCount(estimatedLoadMs, concurrency),
    };
  }

  private estimateLoadMs(): number {
    const average = this.samples.reduce((sum, sample) => sum + sample, 0) / this.samples.length;
    const slowest = Math.max(...this.samples);
    return Math.max(average, slowest * 0.4);
  }

  private resolveAdaptiveConcurrency(estimatedLoadMs: number, maxConcurrency: number): number {
    const desiredConcurrency = Math.ceil(this.adaptiveConcurrencyTargetMs / estimatedLoadMs);
    return this.clamp(desiredConcurrency, 1, Math.min(maxConcurrency, this.adaptiveMaxConcurrency));
  }

  private resolveAdaptiveUrlCount(estimatedLoadMs: number, concurrency: number): number {
    const estimatedOneSecondCapacity = Math.round((this.adaptiveRunwayMs / estimatedLoadMs) * concurrency);
    return this.clamp(
      estimatedOneSecondCapacity,
      this.adaptiveMinAcceptedUrlCount,
      this.adaptiveMaxAcceptedUrlCount,
    );
  }

  private capConcurrency(limits: ImagePreloadLimits, maxConcurrency: number): ImagePreloadLimits {
    return {
      ...limits,
      concurrency: Math.min(maxConcurrency, limits.concurrency),
    };
  }

  private clamp(value: number, min: number, max: number): number {
    return Math.min(Math.max(value, min), max);
  }

  private saveDataEnabled(): boolean {
    const connection = (navigator as Navigator & { connection?: NetworkInformationLike }).connection;
    return connection?.saveData === true;
  }
}
