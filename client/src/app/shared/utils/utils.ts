export function escapeTagName(tagName: string): string {
    return tagName.replace(/:/g, '\\:');
}

export function formatBytes(bytes: number, decimals = 1): string {
    if (!Number.isFinite(bytes) || bytes <= 0) {
        return '0 B';
    }

    const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
    const unitIndex = Math.min(
        Math.floor(Math.log(bytes) / Math.log(1024)),
        units.length - 1,
    );
    const value = bytes / Math.pow(1024, unitIndex);

    if (unitIndex === 0) {
        return `${Math.round(value)} ${units[unitIndex]}`;
    }

    return `${value.toFixed(decimals)} ${units[unitIndex]}`;
}

export function getFileNameFromPath(path: string): string {
    return path.split(/[/\\]/).pop() || path;
}

export function areArraysEqual<T>(
    left: readonly T[],
    right: readonly T[],
    compare: (a: T, b: T) => boolean = Object.is,
): boolean {
    if (left === right) return true;
    if (left.length !== right.length) return false;

    for (let i = 0; i < left.length; i++) {
        if (!compare(left[i], right[i])) {
            return false;
        }
    }

    return true;
}

export function areSetsEqual<T>(left: ReadonlySet<T>, right: ReadonlySet<T>): boolean {
    if (left === right) return true;
    if (left.size !== right.size) return false;

    for (const value of left) {
        if (!right.has(value)) {
            return false;
        }
    }

    return true;
}

/** Generate a unique ID, with fallback for non-secure contexts (HTTP) */
export function generateId(): string {
    if (typeof crypto !== 'undefined' && crypto.randomUUID) {
        return crypto.randomUUID();
    }
    // Fallback for HTTP contexts
    return 'id-' + Math.random().toString(36).substring(2, 11) + Date.now().toString(36);
}

export function getMediaType(contentType: string): 'image' | 'animation' | 'video' {
  if (contentType.startsWith('video/')) {
      return 'video';
  }

  if (contentType === 'image/gif') {
      return 'animation';
  }

  return 'image';
}
