import {
  ChangeDetectionStrategy,
  Component,
  inject,
  input,
  output,
} from "@angular/core";
import { CommonModule } from "@angular/common";
import { BakabooruPostDto } from "@models";
import { ProgressiveImageComponent } from "@shared/components/progressive-image/progressive-image.component";
import { BakabooruService } from "@services/api/bakabooru/bakabooru.service";
import { getMediaType } from "@app/shared/utils/utils";

@Component({
  selector: "app-post-preview-overlay",
  imports: [CommonModule, ProgressiveImageComponent],
  templateUrl: "./post-preview-overlay.component.html",
  styleUrl: "./post-preview-overlay.component.css",
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PostPreviewOverlayComponent {
  private readonly bakabooru = inject(BakabooruService);

  readonly post = input.required<BakabooruPostDto>();
  readonly closed = output<void>();

  getThumbnailUrl(post: BakabooruPostDto): string {
    return this.bakabooru.getThumbnailUrl(
      post.thumbnailLibraryId,
      post.thumbnailContentHash,
    );
  }

  getPostContentUrl(post: BakabooruPostDto): string {
    return this.bakabooru.getPostContentUrl(post.id);
  }

  getMediaType(contentType: string) {
    return getMediaType(contentType);
  }

  onCardLeave(): void {
    this.closed.emit();
  }

  onClick(): void {
    this.closed.emit();
  }
}
