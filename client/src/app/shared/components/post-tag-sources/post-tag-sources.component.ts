import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { PostTagSource } from '@models';
import { TooltipDirective } from '@shared/directives/tooltip.directive';

interface SourceIcon {
  label: string;
  viewBox: string;
  paths: string[];
  /** In viewBox units, so icons with a smaller viewBox need a thinner stroke to match. */
  strokeWidth?: number;
}

// Danbooru's icon is its favicon's outline; Gelbooru's is a plain G, since its logo doesn't work as an outline.
const SOURCE_ICONS: Record<PostTagSource, SourceIcon> = {
  [PostTagSource.Manual]: {
    label: 'Manual',
    viewBox: '0 0 24 24',
    paths: ['M12 20h9', 'M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z'],
  },
  [PostTagSource.Folder]: {
    label: 'Folder',
    viewBox: '0 0 24 24',
    paths: ['M3 6a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z'],
  },
  [PostTagSource.Ai]: {
    label: 'AI',
    viewBox: '0 0 24 24',
    paths: ['M12 3l1.9 5.1L19 10l-5.1 1.9L12 17l-1.9-5.1L5 10l5.1-1.9Z', 'M19 15v4', 'M17 17h4'],
  },
  [PostTagSource.Danbooru]: {
    label: 'Danbooru',
    viewBox: '0 0 16 16',
    strokeWidth: 1.4,
    paths: ['M1.5 14.5V4.25L4.25 1.5H14.5v10.25l-2.75 2.75Z', 'M1.5 4.5h10v10', 'M14.5 1.5l-3 3'],
  },
  [PostTagSource.Gelbooru]: {
    label: 'Gelbooru',
    viewBox: '0 0 24 24',
    strokeWidth: 2.4,
    paths: ['M19.37 6.84A9 9 0 1 0 21 12h-8'],
  },
};

@Component({
  selector: 'app-post-tag-sources',
  standalone: true,
  imports: [TooltipDirective],
  templateUrl: './post-tag-sources.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PostTagSourcesComponent {
  readonly sources = input<PostTagSource[]>([]);

  protected readonly icons = computed(() => Array.from(new Set(this.sources()))
    .sort((a, b) => a - b)
    .map(source => ({ source, ...SOURCE_ICONS[source] })));
}
