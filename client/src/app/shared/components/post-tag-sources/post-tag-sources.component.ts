import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { CommonModule } from '@angular/common';

import { PostTagSource } from '@models';
import { TooltipDirective } from '@shared/directives/tooltip.directive';

type SourceBadgeViewModel = {
  source: PostTagSource;
  label: string;
  icon: string;
  className: string;
};

const SOURCE_BADGES: Record<PostTagSource, Omit<SourceBadgeViewModel, 'source'>> = {
  [PostTagSource.Manual]: { label: 'Manual', icon: 'M', className: 'border-emerald-500/35 bg-emerald-500/12 text-emerald-200' },
  [PostTagSource.Folder]: { label: 'Folder', icon: 'F', className: 'border-sky-500/35 bg-sky-500/12 text-sky-200' },
  [PostTagSource.Ai]: { label: 'AI', icon: 'AI', className: 'border-fuchsia-500/35 bg-fuchsia-500/12 text-fuchsia-200' },
  [PostTagSource.Danbooru]: { label: 'Danbooru', icon: 'D', className: 'border-amber-500/35 bg-amber-500/12 text-amber-100' },
  [PostTagSource.Gelbooru]: { label: 'Gelbooru', icon: 'G', className: 'border-violet-500/35 bg-violet-500/12 text-violet-100' },
};

@Component({
  selector: 'app-post-tag-sources',
  standalone: true,
  imports: [CommonModule, TooltipDirective],
  templateUrl: './post-tag-sources.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PostTagSourcesComponent {
  readonly sources = input<PostTagSource[]>([]);

  readonly badgeModels = () => {
    const unique = Array.from(new Set(this.sources())).sort((a, b) => a - b);
    return unique.map(source => ({ source, ...SOURCE_BADGES[source] satisfies Omit<SourceBadgeViewModel, 'source'> }));
  };

  protected trackBySource = (_: number, badge: SourceBadgeViewModel) => badge.source;
}
