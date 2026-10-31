import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';

import { type DamebooruPostDto } from '@models';
import { DamebooruService } from '@services/api/damebooru/damebooru.service';
import { ProgressiveImageComponent } from '@shared/components/progressive-image/progressive-image.component';
import { getFileNameFromPath, getMediaType } from '@shared/utils/utils';

/** A post's image or video, fitted into whatever box the host gives it. */
@Component({
  selector: 'app-inbox-media',
  standalone: true,
  imports: [ProgressiveImageComponent],
  host: { class: 'relative block overflow-hidden rounded-lg bg-sunken' },
  templateUrl: './inbox-media.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class InboxMediaComponent {
  private readonly damebooru = inject(DamebooruService);

  readonly post = input.required<DamebooruPostDto>();
  readonly autoplay = input(false);

  readonly isVideo = computed(() => getMediaType(this.post().contentType) === 'video');
  readonly previewUrl = computed(() => this.damebooru.getPreviewUrl(this.post().contentHash));
  readonly contentUrl = computed(() => this.damebooru.getPostContentUrl(this.post().id));
  readonly fileName = computed(() => getFileNameFromPath(this.post().relativePath));
}
