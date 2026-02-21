import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
} from "@angular/core";
import { CommonModule } from "@angular/common";
import { DamebooruPostDto } from "@models";
import { ProgressiveImageComponent } from "@shared/components/progressive-image/progressive-image.component";
import { DamebooruService } from "@services/api/damebooru/damebooru.service";
import { getMediaType } from "@app/shared/utils/utils";

@Component({
  selector: "app-post-preview-overlay",
  imports: [CommonModule, ProgressiveImageComponent],
  templateUrl: "./post-preview-overlay.component.html",
  styleUrl: "./post-preview-overlay.component.css",
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PostPreviewOverlayComponent {
  private readonly damebooru = inject(DamebooruService);

  readonly post = input.required<DamebooruPostDto>();
  readonly size = input(90);
  readonly closed = output<void>();

  readonly viewportLimitVw = computed(() => `${this.clampedSizePercent()}vw`);

  readonly viewportLimitVh = computed(() => `${this.clampedSizePercent()}vh`);

  readonly imageAspectRatio = computed(() => {
    const post = this.post();
    return post.width && post.height ? `${post.width}/${post.height}` : "1/1";
  });

  readonly imageContainerWidth = computed(() => {
    const post = this.post();
    const size = this.clampedSizePercent();
    if (!(post.width && post.height)) {
      return `${Math.min(size, 60)}vw`;
    }

    return `min(${size}vw, ${size}vh * ${post.width} / ${post.height})`;
  });

  getThumbnailUrl(post: DamebooruPostDto): string {
    return this.damebooru.getThumbnailUrl(
      post.thumbnailLibraryId,
      post.thumbnailContentHash,
    );
  }

  getPostContentUrl(post: DamebooruPostDto): string {
    return this.damebooru.getPostContentUrl(post.id);
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

  private clampedSizePercent(): number {
    return Math.min(100, Math.max(10, this.size()));
  }
}
