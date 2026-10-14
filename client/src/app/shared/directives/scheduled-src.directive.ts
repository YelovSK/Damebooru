import {
  Directive,
  ElementRef,
  type OnDestroy,
  effect,
  inject,
  input,
} from "@angular/core";

import { SettingsService } from "@services/settings.service";

interface ScheduledImageJob {
  token: symbol;
  element: HTMLImageElement;
  url: string;
  near: boolean;
  farNotBeforeMs: number;
}

class ImageSrcScheduler {
  private static readonly jobs: (ScheduledImageJob | null)[] = [];
  private static readonly pendingByToken = new Map<symbol, ScheduledImageJob>();
  private static readonly nearByToken = new Map<symbol, boolean>();
  private static rafId: number | null = null;
  private static headIndex = 0;
  private static pendingCount = 0;
  private static maxAssignmentsPerFrame = 10;
  private static readonly maxFarAssignmentsPerFrame = 1;
  private static readonly farAssignmentSettleMs = 16;

  static enqueue(job: ScheduledImageJob): void {
    this.cancel(job.token);
    job.near = this.nearByToken.get(job.token) ?? job.near;
    job.farNotBeforeMs = job.near
      ? 0
      : performance.now() + this.farAssignmentSettleMs;
    this.jobs.push(job);
    this.pendingByToken.set(job.token, job);
    this.pendingCount += 1;
    this.requestDrain();
  }

  static setMaxAssignmentsPerFrame(value: number): void {
    if (!Number.isFinite(value)) {
      return;
    }

    this.maxAssignmentsPerFrame = Math.max(1, Math.min(16, Math.floor(value)));
    if (this.pendingCount > 0) {
      this.requestDrain();
    }
  }

  static cancel(token: symbol): void {
    const existing = this.pendingByToken.get(token);
    if (!existing) {
      return;
    }

    this.pendingByToken.delete(token);
    this.pendingCount = Math.max(0, this.pendingCount - 1);
    this.clearPendingState(existing.element);
  }

  static markNear(token: symbol, near: boolean): void {
    this.nearByToken.set(token, near);
    const existing = this.pendingByToken.get(token);
    if (!existing) {
      return;
    }

    existing.near = near;
    if (near) {
      existing.farNotBeforeMs = 0;
      this.requestDrain();
    }
  }

  static forget(token: symbol): void {
    this.cancel(token);
    this.nearByToken.delete(token);
  }

  private static requestDrain(): void {
    if (this.rafId !== null) {
      return;
    }

    this.rafId = requestAnimationFrame(() => {
      this.rafId = null;
      this.drain();
    });
  }

  private static drain(): void {
    let remaining = this.maxAssignmentsPerFrame;
    let remainingFar = this.maxFarAssignmentsPerFrame;
    let blockedByFarLimit = false;

    while (remaining > 0 && this.headIndex < this.jobs.length) {
      const nextIndex = this.pickNextJobIndex(
        remainingFar > 0,
        performance.now(),
      );
      if (nextIndex < 0) {
        this.advanceHead();
        blockedByFarLimit = this.pendingCount > 0 && remainingFar <= 0;
        break;
      }

      const job = this.jobs[nextIndex];
      this.jobs[nextIndex] = null;
      this.advanceHead();
      if (!job) {
        continue;
      }

      const current = this.pendingByToken.get(job.token);
      if (!current || current !== job) {
        continue;
      }

      this.pendingByToken.delete(job.token);
      this.pendingCount = Math.max(0, this.pendingCount - 1);

      if (!job.element.isConnected) {
        continue;
      }

      if (job.element.getAttribute("src") === job.url) {
        this.clearPendingState(job.element);
        continue;
      }

      job.element.src = job.url;
      this.clearPendingState(job.element);
      if (!job.near) {
        remainingFar -= 1;
      }
      remaining -= 1;
    }

    this.compactQueue();

    if (
      this.pendingCount > 0 &&
      (!blockedByFarLimit || this.maxFarAssignmentsPerFrame > 0)
    ) {
      this.requestDrain();
    }
  }

  private static pickNextJobIndex(allowFar: boolean, nowMs: number): number {
    for (let index = this.headIndex; index < this.jobs.length; index += 1) {
      const job = this.jobs[index];
      if (job && this.pendingByToken.get(job.token) === job && job.near) {
        return index;
      }
    }

    if (!allowFar) {
      return -1;
    }

    for (let index = this.headIndex; index < this.jobs.length; index += 1) {
      const job = this.jobs[index];
      if (
        job &&
        this.pendingByToken.get(job.token) === job &&
        job.farNotBeforeMs <= nowMs
      ) {
        return index;
      }
    }

    return -1;
  }

  private static advanceHead(): void {
    while (this.headIndex < this.jobs.length) {
      const job = this.jobs[this.headIndex];
      if (job && this.pendingByToken.get(job.token) === job) {
        return;
      }

      this.jobs[this.headIndex] = null;
      this.headIndex += 1;
    }
  }

  private static compactQueue(): void {
    if (this.headIndex < 128 || this.headIndex * 2 < this.jobs.length) {
      return;
    }

    this.jobs.splice(0, this.headIndex);
    this.headIndex = 0;
  }

  private static clearPendingState(element: HTMLImageElement): void {
    element.style.opacity = "";
  }
}

class NearViewportObserver {
  private static readonly margin = "180px 0px 180px 0px";
  private static readonly callbacks = new Map<Element, (near: boolean) => void>();
  private static observer: IntersectionObserver | null = null;

  static observe(element: Element, callback: (near: boolean) => void): void {
    this.callbacks.set(element, callback);
    this.getObserver().observe(element);
  }

  static unobserve(element: Element): void {
    this.callbacks.delete(element);
    this.observer?.unobserve(element);
  }

  private static getObserver(): IntersectionObserver {
    if (this.observer) {
      return this.observer;
    }

    // scrollMargin extends the clip of scroll containers (the posts viewport); rootMargin only extends the window.
    // TS's DOM lib doesn't know scrollMargin yet.
    const options: IntersectionObserverInit & { scrollMargin: string } = {
      rootMargin: this.margin,
      scrollMargin: this.margin,
      threshold: 0,
    };
    this.observer = new IntersectionObserver((entries) => {
      for (const entry of entries) {
        this.callbacks.get(entry.target)?.(entry.isIntersecting);
      }
    }, options);
    return this.observer;
  }
}

@Directive({
  selector: "img[appScheduledSrc]",
  standalone: true,
})
export class ScheduledSrcDirective implements OnDestroy {
  private readonly elementRef = inject(ElementRef<HTMLImageElement>);
  private readonly settings = inject(SettingsService);
  private readonly token = Symbol("scheduled-image");
  private isNear = false;

  appScheduledSrc = input<string | null | undefined>(null);

  constructor() {
    if (typeof IntersectionObserver === "undefined") {
      this.setNear(true);
    } else {
      NearViewportObserver.observe(this.elementRef.nativeElement, (near) =>
        this.setNear(near),
      );
    }

    effect(() => {
      ImageSrcScheduler.setMaxAssignmentsPerFrame(
        this.settings.performanceSettings().scheduledImageAssignmentsPerFrame,
      );
    });

    effect(() => this.applyScheduledSrc());
  }

  ngOnDestroy(): void {
    ImageSrcScheduler.forget(this.token);
    NearViewportObserver.unobserve(this.elementRef.nativeElement);
  }

  private applyScheduledSrc(): void {
    const url = this.appScheduledSrc()?.trim();
    const element = this.elementRef.nativeElement;

    if (!url) {
      ImageSrcScheduler.cancel(this.token);
      return;
    }

    if (element.getAttribute("src") === url) {
      ImageSrcScheduler.cancel(this.token);
      return;
    }

    if (!this.settings.useScheduledImageSrc()) {
      ImageSrcScheduler.cancel(this.token);
      element.src = url;
      element.style.opacity = "";
      return;
    }

    ImageSrcScheduler.cancel(this.token);
    element.style.opacity = "0";

    ImageSrcScheduler.enqueue({
      token: this.token,
      element,
      url,
      near: this.isNear,
      farNotBeforeMs: 0,
    });
  }

  private setNear(near: boolean): void {
    this.isNear = near;
    ImageSrcScheduler.markNear(this.token, near);
  }
}
