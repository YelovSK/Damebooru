export interface Point2d {
  x: number;
  y: number;
}

export class VelocityTracker {
  private lastPoint: Point2d | null = null;
  private lastTime = 0;
  private currentVelocity: Point2d = { x: 0, y: 0 };

  constructor(private readonly sampleWeight = 0.65) {}

  get velocity(): Point2d {
    return this.currentVelocity;
  }

  reset(point: Point2d, time = performance.now()): void {
    this.currentVelocity = { x: 0, y: 0 };
    this.lastPoint = point;
    this.lastTime = time;
  }

  sample(point: Point2d, time = performance.now()): Point2d {
    if (!this.lastPoint) {
      this.reset(point, time);
      return this.currentVelocity;
    }

    const elapsed = time - this.lastTime;
    if (elapsed <= 0) {
      return this.currentVelocity;
    }

    const nextVelocity = {
      x: (point.x - this.lastPoint.x) / elapsed,
      y: (point.y - this.lastPoint.y) / elapsed,
    };
    const previousWeight = 1 - this.sampleWeight;

    this.currentVelocity = {
      x: this.currentVelocity.x * previousWeight + nextVelocity.x * this.sampleWeight,
      y: this.currentVelocity.y * previousWeight + nextVelocity.y * this.sampleWeight,
    };
    this.lastPoint = point;
    this.lastTime = time;

    return this.currentVelocity;
  }
}
