import { Injectable, signal } from '@angular/core';
import { Observable, Subscriber } from 'rxjs';

import { ButtonVariant } from '@shared/components/button/button.component';

export interface ConfirmOptions {
  title: string;
  message: string;
  confirmText?: string;
  cancelText?: string;
  variant?: ButtonVariant;
  requireTypedText?: string;
}

@Injectable({
  providedIn: 'root'
})
export class ConfirmService {
  private activeRequest: ConfirmRequest | null = null;
  private pendingRequests: ConfirmRequest[] = [];

  options = signal<ConfirmOptions | null>(null);

  confirm(options: ConfirmOptions): Observable<boolean> {
    return new Observable<boolean>((subscriber) => {
      const request: ConfirmRequest = { options, subscriber };
      this.pendingRequests.push(request);
      this.showNextRequest();

      return () => {
        this.pendingRequests = this.pendingRequests.filter((r) => r !== request);
      };
    });
  }

  resolve(result: boolean): void {
    const active = this.activeRequest;
    if (!active) {
      return;
    }

    this.activeRequest = null;
    this.options.set(null);

    active.subscriber.next(result);
    active.subscriber.complete();

    // Defer the next dialog to avoid click-event bleed-through.
    setTimeout(() => this.showNextRequest(), 0);
  }

  private showNextRequest(): void {
    if (this.activeRequest || this.pendingRequests.length === 0) {
      return;
    }

    const next = this.pendingRequests.shift();
    if (!next) {
      return;
    }

    this.activeRequest = next;
    this.options.set(next.options);
  }
}

interface ConfirmRequest {
  options: ConfirmOptions;
  subscriber: Subscriber<boolean>;
}
