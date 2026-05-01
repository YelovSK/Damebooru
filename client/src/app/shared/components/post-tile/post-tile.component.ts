import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
} from "@angular/core";
import { RouterLink, Params } from "@angular/router";

import { AppPaths } from "@app/app.paths";
import { DamebooruPostDto } from "@models";
import { DamebooruService } from "@services/api/damebooru/damebooru.service";
import { getMediaType } from "@shared/utils/utils";
import { ScheduledSrcDirective } from "@shared/directives/scheduled-src.directive";

@Component({
  selector: "app-post-tile",
  standalone: true,
  imports: [RouterLink, ScheduledSrcDirective],
  templateUrl: "./post-tile.component.html",
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PostTileComponent {
  private readonly damebooru = inject(DamebooruService);

  readonly appPaths = AppPaths;

  post = input.required<DamebooruPostDto>();
  queryParams = input<Params | null>(null);

  readonly postId = computed(() => this.post().id);
  readonly isFavorite = computed(() => this.post().isFavorite);
  readonly thumbnailUrl = computed(() => {
    const post = this.post();
    return this.damebooru.getThumbnailUrl(
      post.thumbnailLibraryId,
      post.thumbnailContentHash,
    );
  });
  readonly mediaType = computed(() => getMediaType(this.post().contentType));
}
