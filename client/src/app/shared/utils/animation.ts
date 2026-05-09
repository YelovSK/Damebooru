export interface AnimationHandle {
  cancel(): void;
}

export interface AnimateValueOptions<T> {
  from: T;
  to: T;
  duration: number;
  interpolate: (from: T, to: T, progress: number) => T;
  onUpdate: (value: T) => void;
  easing?: (progress: number) => number;
  onComplete?: () => void;
}

export interface ValueAnimatorOptions<T> {
  interpolate: (from: T, to: T, progress: number) => T;
  onUpdate: (value: T) => void;
  easing?: (progress: number) => number;
  onComplete?: () => void;
}

export class ValueAnimator<T> {
  private handle: AnimationHandle | null = null;
  private currentTarget: T | null = null;

  constructor(private readonly options: ValueAnimatorOptions<T>) {}

  get isAnimating(): boolean {
    return this.handle !== null;
  }

  get target(): T | null {
    return this.currentTarget;
  }

  animate(from: T, to: T, duration: number): void {
    this.cancel();
    this.currentTarget = to;

    if (duration <= 0) {
      this.options.onUpdate(to);
      this.currentTarget = null;
      this.options.onComplete?.();
      return;
    }

    const handle = animateValue({
      from,
      to,
      duration,
      interpolate: this.options.interpolate,
      easing: this.options.easing,
      onUpdate: this.options.onUpdate,
      onComplete: () => {
        if (this.handle === handle) {
          this.handle = null;
          this.currentTarget = null;
        }
        this.options.onComplete?.();
      },
    });
    this.handle = handle;
  }

  cancel(): void {
    this.handle?.cancel();
    this.handle = null;
    this.currentTarget = null;
  }
}

export function animateValue<T>({
  from,
  to,
  duration,
  interpolate,
  onUpdate,
  easing = linear,
  onComplete,
}: AnimateValueOptions<T>): AnimationHandle {
  let frameId: number | null = null;
  let cancelled = false;
  const startTime = performance.now();

  const cancel = (): void => {
    cancelled = true;
    if (frameId !== null) {
      cancelAnimationFrame(frameId);
      frameId = null;
    }
  };

  if (duration <= 0) {
    onUpdate(to);
    onComplete?.();
    return { cancel };
  }

  const step = (time: number): void => {
    if (cancelled) {
      return;
    }

    const progress = Math.min(1, (time - startTime) / duration);
    onUpdate(interpolate(from, to, easing(progress)));

    if (progress >= 1) {
      frameId = null;
      onComplete?.();
      return;
    }

    frameId = requestAnimationFrame(step);
  };

  frameId = requestAnimationFrame(step);

  return { cancel };
}

export function lerpNumber(from: number, to: number, progress: number): number {
  return from + (to - from) * progress;
}

export function linear(progress: number): number {
  return progress;
}

export function easeOutCubic(progress: number): number {
  return 1 - Math.pow(1 - progress, 3);
}
