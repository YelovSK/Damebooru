import {
  Directive,
  ElementRef,
  OnDestroy,
  effect,
  inject,
  input,
} from "@angular/core";

type ScheduledImageJob = {
  token: symbol;
  element: HTMLImageElement;
  url: string;
};

/**
 * Used for throttling DOM manipulation, specifically setting the img src attribute.
 * When using CDK and scrolling very quickly, the performance goes to shit due to the
 * many img.src assignments, so this directive is used for throttling it to some number
 * of assignments per frame.
 * Or in other words, prioritizes smoothness over the speed of loading.
 */
class ImageSrcScheduler {
  private static readonly jobs: ScheduledImageJob[] = [];
  private static readonly pendingByToken = new Map<symbol, ScheduledImageJob>();
  private static rafId: number | null = null;
  private static readonly maxAssignmentsPerFrame = 10;

  static enqueue(job: ScheduledImageJob): void {
    this.cancel(job.token);
    this.jobs.push(job);
    this.pendingByToken.set(job.token, job);
    this.requestDrain();
  }

  static cancel(token: symbol): void {
    const existing = this.pendingByToken.get(token);
    if (!existing) {
      return;
    }

    this.pendingByToken.delete(token);
    const index = this.jobs.indexOf(existing);
    if (index >= 0) {
      this.jobs.splice(index, 1);
    }

    this.clearPendingState(existing.element);
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

    while (remaining > 0 && this.jobs.length > 0) {
      const nextIndex = this.pickNextJobIndex();
      const [job] = this.jobs.splice(nextIndex, 1);
      if (!job) {
        break;
      }

      const current = this.pendingByToken.get(job.token);
      if (!current || current !== job) {
        continue;
      }

      this.pendingByToken.delete(job.token);

      if (!job.element.isConnected) {
        continue;
      }

      if (job.element.getAttribute("src") === job.url) {
        this.clearPendingState(job.element);
        continue;
      }

      job.element.src = job.url;
      this.clearPendingState(job.element);
      remaining -= 1;
    }

    if (this.jobs.length > 0) {
      this.requestDrain();
    }
  }

  private static pickNextJobIndex(): number {
    for (let index = 0; index < this.jobs.length; index += 1) {
      const job = this.jobs[index];
      if (this.pendingByToken.get(job.token) !== job) {
        continue;
      }

      if (job.element.getAttribute("data-scheduled-near") === "true") {
        return index;
      }
    }

    for (let index = 0; index < this.jobs.length; index += 1) {
      const job = this.jobs[index];
      if (this.pendingByToken.get(job.token) !== job) {
        continue;
      }

      if (this.isLikelyVisible(job.element)) {
        return index;
      }
    }

    return 0;
  }

  private static isLikelyVisible(element: HTMLImageElement): boolean {
    if (!element.isConnected) {
      return false;
    }

    const rect = element.getBoundingClientRect();
    const margin = 160;

    return (
      rect.bottom >= -margin &&
      rect.top <= window.innerHeight + margin &&
      rect.right >= -margin &&
      rect.left <= window.innerWidth + margin
    );
  }

  private static clearPendingState(element: HTMLImageElement): void {
    element.removeAttribute("data-scheduled-pending");
    element.style.opacity = "";
  }
}

@Directive({
  selector: "img[scheduledSrc]",
  standalone: true,
})
export class ScheduledSrcDirective implements OnDestroy {
  private static readonly observerRootMargin = "180px 0px 180px 0px";

  private readonly elementRef = inject(ElementRef<HTMLImageElement>);
  private readonly token = Symbol("scheduled-image");
  private intersectionObserver?: IntersectionObserver;

  scheduledSrc = input<string | null | undefined>(null);

  constructor() {
    this.setupIntersectionObserver();

    effect(() => {
      const url = this.scheduledSrc()?.trim();
      const element = this.elementRef.nativeElement;
      if (!url) {
        ImageSrcScheduler.cancel(this.token);
        return;
      }

      if (element.getAttribute("src") === url) {
        ImageSrcScheduler.cancel(this.token);
        return;
      }

      ImageSrcScheduler.cancel(this.token);
      element.setAttribute("data-scheduled-pending", "true");
      element.style.opacity = "0";

      ImageSrcScheduler.enqueue({
        token: this.token,
        element,
        url,
      });
    });
  }

  ngOnDestroy(): void {
    ImageSrcScheduler.cancel(this.token);
    this.intersectionObserver?.disconnect();
    this.intersectionObserver = undefined;
  }

  private setupIntersectionObserver(): void {
    const element = this.elementRef.nativeElement;

    if (typeof IntersectionObserver === "undefined") {
      element.setAttribute("data-scheduled-near", "true");
      return;
    }

    const root = element.closest(".posts-virtual-viewport");

    this.intersectionObserver = new IntersectionObserver(
      (entries) => {
        const entry = entries[entries.length - 1];
        element.setAttribute(
          "data-scheduled-near",
          entry?.isIntersecting ? "true" : "false",
        );
      },
      {
        root,
        rootMargin: ScheduledSrcDirective.observerRootMargin,
        threshold: 0,
      },
    );

    this.intersectionObserver.observe(element);
  }
}
