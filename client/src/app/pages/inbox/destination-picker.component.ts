import { ChangeDetectionStrategy, Component, type ElementRef, computed, inject, input, output, signal, viewChild } from '@angular/core';

import { type FolderLocation } from '@models';

import { InboxStore, folderKey } from './inbox.store';

const MAX_RESULTS = 8;

interface SearchResult {
  location: FolderLocation;
  label: string;
  postCount: number | null;
}

/**
 * Chips for the chosen destinations plus a folder search. Keys it doesn't use bubble up, so a page can add its own:
 * it handles arrows and Escape while searching, Enter to add the highlighted folder (or `commit` when there is none),
 * and Backspace on an empty search to remove the last destination.
 */
@Component({
  selector: 'app-destination-picker',
  standalone: true,
  templateUrl: './destination-picker.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DestinationPickerComponent {
  readonly store = inject(InboxStore);

  readonly selected = input.required<FolderLocation[]>();
  readonly disabled = input(false);
  readonly added = output<FolderLocation>();
  readonly removed = output<FolderLocation>();
  readonly commit = output<void>();

  readonly query = signal('');
  readonly highlighted = signal(-1);

  private readonly input = viewChild<ElementRef<HTMLInputElement>>('search');

  /** Rebuilt only when the folders change, not on every keystroke. */
  private readonly searchable = computed(() => this.store.destinations().map(d => {
    const label = this.store.label(d);
    return { location: { libraryId: d.libraryId, folder: d.folder }, label, search: label.toLowerCase(), postCount: d.postCount };
  }));
  private readonly folderNames = computed(() => new Set(this.store.destinations().map(d => d.folder.toLowerCase())));

  readonly results = computed<SearchResult[]>(() => {
    const query = this.query().trim().toLowerCase();
    if (!query) {
      return this.store.recent().map(location => ({ location, label: this.store.label(location), postCount: null }));
    }

    const tokens = query.split(/\s+/);
    const matches = this.searchable()
      .filter(r => tokens.every(t => r.search.includes(t)))
      .sort((a, b) => rankMatch(b, query) - rankMatch(a, query) || (b.postCount ?? 0) - (a.postCount ?? 0))
      .slice(0, MAX_RESULTS);

    const typed = this.query().trim().replace(/\\/g, '/').replace(/^\/+|\/+$/g, '');
    const exists = this.folderNames().has(typed.toLowerCase());
    if (exists || !typed) {
      return matches;
    }

    const created = [...this.store.libraryNames().keys()].map(libraryId => {
      const location = { libraryId, folder: typed };
      return { location, label: `New folder: ${this.store.label(location)}`, postCount: null };
    });
    return [...matches, ...created];
  });

  focus(): void {
    queueMicrotask(() => this.focusNow());
  }

  /** Focusing during a keydown makes the key's character land in the search. */
  focusNow(): void {
    this.input()?.nativeElement.focus();
  }

  reset(): void {
    this.setQuery('');
  }

  setQuery(value: string): void {
    this.query.set(value);
    this.highlighted.set(value.trim() ? 0 : -1);
  }

  pick(result: SearchResult): void {
    if (!this.selected().some(s => folderKey(s) === folderKey(result.location))) {
      this.added.emit(result.location);
    }

    this.setQuery('');
    this.focus();
  }

  remove(location: FolderLocation): void {
    this.removed.emit(location);
    this.focus();
  }

  onKeydown(event: KeyboardEvent): void {
    const results = this.results();
    const empty = this.query().length === 0;

    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        this.highlighted.update(h => Math.min(results.length - 1, h + 1));
        return;
      case 'ArrowUp':
        event.preventDefault();
        this.highlighted.update(h => h === -1 && results.length > 0 ? 0 : Math.max(-1, h - 1));
        return;
      case 'Enter': {
        if (event.ctrlKey) return;
        event.preventDefault();
        const result = results[this.highlighted()];
        if (result) {
          this.pick(result);
        } else if (empty) {
          this.commit.emit();
        }

        return;
      }
      case 'Escape':
        if (empty && this.highlighted() === -1) return;
        event.preventDefault();
        this.setQuery('');
        return;
      case 'Backspace': {
        const last = this.selected().at(-1);
        if (empty && last) {
          event.preventDefault();
          this.removed.emit(last);
        }

        return;
      }
    }
  }
}

function rankMatch(result: SearchResult, query: string): number {
  const lastSegment = result.location.folder.split('/').pop()?.toLowerCase() ?? '';
  if (lastSegment === query) return 2;
  if (lastSegment.startsWith(query)) return 1;
  return 0;
}
