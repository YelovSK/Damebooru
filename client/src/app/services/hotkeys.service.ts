import { Injectable, inject } from '@angular/core';
import { DOCUMENT } from '@angular/common';
import { fromEvent, type Observable } from 'rxjs';
import { filter, share, map } from 'rxjs/operators';

export interface HotkeyOptions {
    element?: HTMLElement;
    keys: string;
    allowInInput?: boolean;
}

@Injectable({
    providedIn: 'root'
})
export class HotkeysService {
    private document = inject(DOCUMENT);

    // Global keydown stream
    private keyDown$ = fromEvent<KeyboardEvent>(this.document, 'keydown').pipe(
        share()
    );

    /**
     * Listen for a specific key combination.
     * @param key The key to listen for (e.g., 'Enter', 'Escape', 'ArrowLeft', 'f')
     * @param modifiers Optional required modifiers
     */
    on(key: string, options?: {
        ctrl?: boolean;
        alt?: boolean;
        shift?: boolean;
        meta?: boolean;
        allowInInput?: boolean;
        preventDefault?: boolean;
    }): Observable<KeyboardEvent> {
        const k = key.toLowerCase();

        return this.keyDown$.pipe(
            filter(event => {
                // 1. Check modifiers
                if (options?.ctrl && !event.ctrlKey) return false;
                if (options?.alt && !event.altKey) return false;
                if (options?.shift && !event.shiftKey) return false;
                if (options?.meta && !event.metaKey) return false;

                // 2. Check Key
                if (event.key.toLowerCase() !== k) return false;

                // 3. Check Input Safety (default: blocking input)
                if (!options?.allowInInput) {
                    const target = event.target as HTMLElement;
                    const isInput = ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName);
                    if (isInput) return false;
                }

                // 4. Prevent Default if requested
                if (options?.preventDefault) {
                    event.preventDefault();
                }

                return true;
            })
        );
    }

    /**
     * Shortcut for common specific key requirements
     */
    ctrl(key: string): Observable<KeyboardEvent> {
        return this.on(key, { ctrl: true, preventDefault: true });
    }
}
