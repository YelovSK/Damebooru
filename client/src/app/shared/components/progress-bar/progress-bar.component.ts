import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

@Component({
  selector: 'app-progress-bar',
  standalone: true,
  templateUrl: './progress-bar.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ProgressBarComponent {
  progress = input<number>(0);
  normalizeRatio = input<boolean>(false);
  heightClass = input<string>('h-2');
  trackClass = input<string>('bg-black/10');
  fillClass = input<string>('bg-accent-primary');
  roundedClass = input<string>('rounded-full');
  animated = input<boolean>(true);
  indeterminate = input<boolean>(false);

  percent = computed(() => {
    const raw = this.progress();
    if (!Number.isFinite(raw)) return 0;
    const normalized = this.normalizeRatio() && raw <= 1 ? raw * 100 : raw;
    return Math.max(0, Math.min(100, normalized));
  });

  trackClasses = computed(
    () => `w-full ${this.trackClass()} ${this.heightClass()} ${this.roundedClass()} overflow-hidden`
  );

  fillClasses = computed(
    () => `${this.fillClass()} h-full ${this.roundedClass()} ${this.animated() ? 'transition-all duration-300' : ''}`
  );

  indeterminateFillClasses = computed(
    () => `${this.fillClass()} h-full w-1/3 ${this.roundedClass()} animate-pulse`
  );
}
