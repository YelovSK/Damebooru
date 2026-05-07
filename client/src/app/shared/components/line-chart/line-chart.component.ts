import { afterNextRender, ChangeDetectionStrategy, Component, DestroyRef, ElementRef, computed, inject, input, signal, viewChild } from '@angular/core';

export interface LineChartPoint {
  label: string;
  value: number;
}

interface ChartPoint extends LineChartPoint {
  x: number;
  y: number;
}

interface ChartTick {
  index: number;
  label: string;
  y: number;
}

interface ChartLabel {
  index: number;
  label: string;
  x: number;
}

interface ChartBounds {
  min: number;
  max: number;
}

interface ActiveChartPoint extends ChartPoint {
  formattedValue: string;
  tooltipXPercent: number;
  tooltipYPercent: number;
  tooltipAlign: 'left' | 'center' | 'right';
  tooltipVerticalAlign: 'above' | 'below';
  tooltipTransform: string;
}

@Component({
  selector: 'app-line-chart',
  standalone: true,
  templateUrl: './line-chart.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LineChartComponent {
  private static nextId = 0;
  private readonly destroyRef = inject(DestroyRef);

  readonly points = input<readonly LineChartPoint[]>([]);
  readonly title = input('');
  readonly description = input('');
  readonly emptyLabel = input('No chart data yet');
  readonly valueFormatter = input<(value: number) => string>((value) => value.toLocaleString());
  readonly showArea = input(true);
  readonly chartHeight = input(280);

  readonly chartSurface = viewChild.required<ElementRef<HTMLElement>>('chartSurface');
  readonly activePointIndex = signal<number | null>(null);
  readonly measuredWidth = signal(720);

  readonly gradientId = `line-chart-area-gradient-${LineChartComponent.nextId}`;
  readonly glowId = `line-chart-glow-${LineChartComponent.nextId++}`;

  readonly padding = {
    top: 18,
    right: 18,
    bottom: 34,
    left: 58,
  } as const;

  readonly width = computed(() => Math.max(320, this.measuredWidth()));
  readonly height = computed(() => Math.max(160, this.chartHeight()));
  readonly viewBox = computed(() => `0 0 ${this.width()} ${this.height()}`);
  readonly plotWidth = computed(() => this.width() - this.padding.left - this.padding.right);
  readonly plotHeight = computed(() => this.height() - this.padding.top - this.padding.bottom);
  readonly baselineY = computed(() => this.padding.top + this.plotHeight());

  constructor() {
    afterNextRender(() => {
      const element = this.chartSurface().nativeElement;
      const syncWidth = () => {
        const width = Math.round(element.getBoundingClientRect().width);
        if (width > 0) {
          this.measuredWidth.set(width);
        }
      };

      syncWidth();

      const resizeObserver = new ResizeObserver(syncWidth);
      resizeObserver.observe(element);
      this.destroyRef.onDestroy(() => resizeObserver.disconnect());
    });
  }

  readonly sanitizedPoints = computed(() =>
    this.points().filter(point => Number.isFinite(point.value))
  );

  readonly hasData = computed(() => this.sanitizedPoints().length > 0);

  readonly bounds = computed<ChartBounds>(() => {
    const values = this.sanitizedPoints().map(point => point.value);

    if (values.length === 0) {
      return { min: 0, max: 1 };
    }

    const rawMin = Math.min(...values);
    const rawMax = Math.max(...values);
    const min = rawMin < 0 ? rawMin : 0;
    let max = rawMax;

    if (max === min) {
      max = min + 1;
    }

    return { min, max };
  });

  readonly chartPoints = computed<readonly ChartPoint[]>(() => {
    const points = this.sanitizedPoints();
    const bounds = this.bounds();
    const range = bounds.max - bounds.min;
    const plotWidth = this.plotWidth();
    const xStep = points.length > 1 ? plotWidth / (points.length - 1) : 0;
    const plotHeight = this.plotHeight();

    return points.map((point, index) => ({
      ...point,
      x: this.padding.left + (points.length > 1 ? index * xStep : plotWidth / 2),
      y: this.padding.top + plotHeight - ((point.value - bounds.min) / range) * plotHeight,
    }));
  });

  readonly linePath = computed(() => {
    const points = this.chartPoints();

    return points
      .map((point, index) => `${index === 0 ? 'M' : 'L'} ${point.x.toFixed(2)} ${point.y.toFixed(2)}`)
      .join(' ');
  });

  readonly areaPath = computed(() => {
    const points = this.chartPoints();

    if (points.length === 0) {
      return '';
    }

    const first = points[0];
    const last = points[points.length - 1];
    const baselineY = this.baselineY();

    return `${this.linePath()} L ${last.x.toFixed(2)} ${baselineY.toFixed(2)} L ${first.x.toFixed(2)} ${baselineY.toFixed(2)} Z`;
  });

  readonly yTicks = computed<readonly ChartTick[]>(() => {
    const bounds = this.bounds();
    const formatter = this.valueFormatter();
    const tickCount = 5;
    const plotHeight = this.plotHeight();

    return Array.from({ length: tickCount }, (_, index) => {
      const ratio = index / (tickCount - 1);
      const value = bounds.max - (bounds.max - bounds.min) * ratio;

      return {
        index,
        label: formatter(value),
        y: this.padding.top + plotHeight * ratio,
      };
    });
  });

  readonly xLabels = computed<readonly ChartLabel[]>(() => {
    const points = this.chartPoints();

    if (points.length <= 3) {
      return points.map((point, index) => ({ index, label: point.label, x: point.x }));
    }

    const middleIndex = Math.floor((points.length - 1) / 2);
    const indexes = [0, middleIndex, points.length - 1];

    return indexes.map(index => ({
      index,
      label: points[index].label,
      x: points[index].x,
    }));
  });

  readonly activePoint = computed<ActiveChartPoint | null>(() => {
    const index = this.activePointIndex();
    const points = this.chartPoints();

    if (index === null || !points[index]) {
      return null;
    }

    const point = points[index];
    const width = this.width();
    const xPercent = (point.x / width) * 100;

    const horizontalTransform = xPercent < 24 ? '0' : xPercent > 76 ? '-100%' : '-50%';
    const verticalAlign = point.y < this.height() / 2 ? 'below' : 'above';
    const verticalTransform = verticalAlign === 'below' ? '0.75rem' : 'calc(-100% - 0.75rem)';

    return {
      ...point,
      formattedValue: this.valueFormatter()(point.value),
      tooltipXPercent: xPercent,
      tooltipYPercent: (point.y / this.height()) * 100,
      tooltipAlign: xPercent < 24 ? 'left' : xPercent > 76 ? 'right' : 'center',
      tooltipVerticalAlign: verticalAlign,
      tooltipTransform: `translate(${horizontalTransform}, ${verticalTransform})`,
    };
  });

  onPointerMove(event: PointerEvent): void {
    const svg = event.currentTarget as SVGSVGElement | null;
    const points = this.chartPoints();

    if (!svg || points.length === 0) {
      this.activePointIndex.set(null);
      return;
    }

    const rect = svg.getBoundingClientRect();
    const ratio = (event.clientX - rect.left) / rect.width;
    const chartX = ratio * this.width();
    let nearestIndex = 0;
    let nearestDistance = Number.POSITIVE_INFINITY;

    points.forEach((point, index) => {
      const distance = Math.abs(point.x - chartX);
      if (distance < nearestDistance) {
        nearestDistance = distance;
        nearestIndex = index;
      }
    });

    this.activePointIndex.set(nearestIndex);
  }

  onPointerLeave(): void {
    this.activePointIndex.set(null);
  }
}
