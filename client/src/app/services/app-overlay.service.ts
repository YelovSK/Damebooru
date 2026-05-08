import { Injectable, inject } from '@angular/core';
import { Overlay, type OverlayRef } from '@angular/cdk/overlay';

@Injectable({
  providedIn: 'root',
})
export class AppOverlayService {
  private readonly overlay = inject(Overlay);

  createCenteredModal(): OverlayRef {
    return this.overlay.create({
      backdropClass: 'app-overlay-backdrop',
      hasBackdrop: true,
      panelClass: 'app-centered-modal-panel',
      positionStrategy: this.overlay
        .position()
        .global()
        .centerHorizontally()
        .centerVertically(),
      scrollStrategy: this.overlay.scrollStrategies.block(),
    });
  }

  createBottomSheet(): OverlayRef {
    return this.overlay.create({
      backdropClass: 'app-bottom-sheet-backdrop',
      hasBackdrop: true,
      panelClass: 'app-bottom-sheet-panel',
      positionStrategy: this.overlay
        .position()
        .global()
        .left('0')
        .right('0')
        .bottom('0'),
      scrollStrategy: this.overlay.scrollStrategies.noop(),
    });
  }
}
