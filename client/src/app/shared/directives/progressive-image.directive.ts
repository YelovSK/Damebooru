import { DestroyRef, Directive, ElementRef, Renderer2, effect, inject, input } from '@angular/core';

@Directive({
  selector: 'img[appProgressiveImage]',
  standalone: true,
})
export class ProgressiveImageDirective {
  readonly fullSrc = input<string | null>(null, { alias: 'appProgressiveImage' });

  private requestId = 0;
  private readonly destroyRef = inject(DestroyRef);

  constructor(
    private readonly el: ElementRef<HTMLImageElement>,
    private readonly renderer: Renderer2,
  ) {
    effect(() => {
      const src = this.fullSrc();
      this.loadFullImage(src);
    });

    this.destroyRef.onDestroy(() => {
      this.requestId++;
    });
  }

  private loadFullImage(fullSrc: string | null): void {
    const target = fullSrc?.trim();
    if (!target) return;

    const host = this.el.nativeElement;
    
    // Full source already displayed.
    if (host.getAttribute('src') === target) {
      return;
    }

    const req = ++this.requestId;
    const loader = new Image();
    
    loader.onload = async () => {
      if (req !== this.requestId) return;
      try { await loader.decode(); } catch {}
      if (req !== this.requestId) return;

      this.renderer.setAttribute(host, 'src', target);
    };

    loader.onerror = () => {
      if (req !== this.requestId) return;
    };

    loader.src = target;
  }
}
