import { Injectable } from '@angular/core';

export const STORAGE_KEYS = {
    // Auth
    USERNAME: 'damebooru_username',
    TOKEN: 'damebooru_token',

    // UI Preferences
    POSTS_PAGE_SIZE: 'posts_pageSize',
    POSTS_THUMBNAIL_SIZE: 'posts_thumbnailSize',
    POSTS_GRID_COLUMNS: 'posts_gridColumns',
    POSTS_GRID_DENSITY: 'posts_gridDensity',
    // Legacy key kept for migration only.
    POSTS_GRID_SIZE_INDEX: 'posts_gridSizeIndex',

    // Settings
    POST_SETTINGS: 'damebooru_post_settings',

    // Prefixes
    AUTO_TAGGING_SETTINGS: 'damebooru_at_settings_',
} as const;

export type StorageKey = typeof STORAGE_KEYS[keyof typeof STORAGE_KEYS];

@Injectable({
    providedIn: 'root'
})
export class StorageService {

    getItem(key: string): string | null {
        return localStorage.getItem(key);
    }

    setItem(key: string, value: string): void {
        localStorage.setItem(key, value);
    }

    removeItem(key: string): void {
        localStorage.removeItem(key);
    }

    // Typed helpers for specific types
    getNumber(key: string): number | null {
        const val = this.getItem(key);
        return val ? Number(val) : null;
    }

    setNumber(key: string, value: number): void {
        this.setItem(key, value.toString());
    }

    getJson<T>(key: string): T | null {
        const val = this.getItem(key);
        if (!val) return null;
        try {
            return JSON.parse(val) as T;
        } catch {
            console.warn(`StorageService: Removing corrupted data for key ${key}`);
            this.removeItem(key);
            return null;
        }
    }

    setJson<T>(key: string, value: T): void {
        try {
            this.setItem(key, JSON.stringify(value));
        } catch (e) {
            console.error(`StorageService: Failed to stringify JSON for key ${key}`, e);
        }
    }
}
