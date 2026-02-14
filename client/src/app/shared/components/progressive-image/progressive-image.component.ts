import { ChangeDetectionStrategy, Component, computed, effect, input, signal } from '@angular/core';

@Component({
  selector: 'app-progressive-image',
  standalone: true,
  templateUrl: './progressive-image.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ProgressiveImageComponent {
  readonly thumbnailSrc = input.required<string>();
  readonly fullSrc = input.required<string>();
  readonly alt = input<string>('');
  readonly width = input<number | null>(null);
  readonly height = input<number | null>(null);

  readonly fullVisible = signal(false);
  readonly loadToken = signal(0);

  readonly aspectRatio = computed(() => {
    if (this.width() && this.height()) {
      return this.width()! / this.height()!;
    }

    return null;
  });

  constructor() {
    effect(() => {
      // Reset transition state whenever either source changes.
      this.thumbnailSrc();
      this.fullSrc();
      this.fullVisible.set(false);
      this.loadToken.update(value => value + 1);
    });
  }

  async onFullLoad(event: Event, tokenAtEvent: number): Promise<void> {
    const img = event.target as HTMLImageElement;

    try {
      await img.decode();
    } catch {
      // decode() can reject for cross-origin/cached edge cases; keep graceful behavior.
    }

    if (tokenAtEvent !== this.loadToken()) {
      return;
    }

    this.fullVisible.set(true);
  }
}
