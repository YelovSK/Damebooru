import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

export type PageWidth = 'default' | 'wide';

const widthClasses: Record<PageWidth, string> = {
  default: 'max-w-6xl',
  wide: 'max-w-[1400px]',
};

@Component({
  selector: 'app-page',
  standalone: true,
  templateUrl: './page.component.html',
  host: {
    class: 'mx-auto block w-full px-4 py-6 md:px-6',
    '[class]': 'widthClass()',
  },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PageComponent {
  readonly heading = input.required<string>();
  readonly description = input('');
  readonly width = input<PageWidth>('default');

  protected readonly widthClass = computed(() => widthClasses[this.width()]);
}
