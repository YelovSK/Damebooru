import { Injectable, signal } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class PostPreviewHoverGateService {
  private readonly suppressed = signal(false);

  readonly isSuppressed = this.suppressed.asReadonly();

  suppressUntilPointerMove(): void {
    this.suppressed.set(true);
  }

  resumeIfSuppressed(): boolean {
    if (!this.suppressed()) {
      return false;
    }

    this.suppressed.set(false);
    return true;
  }
}
