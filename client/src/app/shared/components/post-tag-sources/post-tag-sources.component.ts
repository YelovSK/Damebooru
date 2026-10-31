import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { CommonModule } from '@angular/common';

import { PostTagSource } from '@models';
import { TooltipDirective } from '@shared/directives/tooltip.directive';

interface SourceBadgeViewModel {
  source: PostTagSource;
  label: string;
  icon: string;
  className: string;
}

const SOURCE_BADGES: Record<PostTagSource, Omit<SourceBadgeViewModel, 'source'>> = {
  [PostTagSource.Manual]: { label: 'Manual', icon: 'M', className: 'border-success/35 bg-success/12 text-success' },
  [PostTagSource.Folder]: { label: 'Folder', icon: 'F', className: 'border-accent/35 bg-accent/12 text-accent' },
  [PostTagSource.Ai]: { label: 'AI', icon: 'AI', className: 'border-tag-artist/35 bg-tag-artist/12 text-tag-artist' },
  [PostTagSource.Danbooru]: { label: 'Danbooru', icon: 'D', className: 'border-warning/35 bg-warning/12 text-warning' },
  [PostTagSource.Gelbooru]: { label: 'Gelbooru', icon: 'G', className: 'border-tag-meta/35 bg-tag-meta/12 text-tag-meta' },
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
