import { Component, ChangeDetectionStrategy, signal, input, output, effect, HostListener, ElementRef, viewChild, viewChildren, contentChild, TemplateRef, untracked, inject, DestroyRef } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { toObservable, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { debounceTime, distinctUntilChanged, filter, fromEvent, switchMap } from 'rxjs';
import { HotkeysService } from '@services/hotkeys.service';

export interface FocusShortcut {
  modifier: 'ctrl' | 'meta' | null;
  key: string;
}

@Component({
  selector: 'app-autocomplete',
  standalone: true,
  imports: [NgTemplateOutlet],
  templateUrl: './autocomplete.component.html',
  styleUrl: './autocomplete.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class AutocompleteComponent<T> {
  // Inputs
  value = input<string>('');
  placeholder = input<string>('Search...');
  suggestions = input<T[]>([]);
  multi = input<boolean>(false);
  debounce = input<number>(300);
  focusShortcut = input<FocusShortcut | null>(null);
  showClear = input<boolean>(true);

  // Custom Template for items
  itemTemplate = contentChild(TemplateRef);

  // Outputs
  searchTrigger = output<string>();         // Triggered on Enter (when no selection active)
  queryChange = output<string>();    // Triggered as user types (debounced)
  selection = output<T>();           // Triggered when an item is chosen
  valueChange = output<string>();    // Continuous raw value change

  // State
  inputValue = signal('');
  isDropdownOpen = signal(false);
  selectedIndex = signal(-1);
  manualClose = signal(false);
  internalLoading = signal(false);

  private inputElement = viewChild<ElementRef<HTMLInputElement>>('searchInput');
  private listElement = viewChild<ElementRef<HTMLUListElement>>('suggestionList');

  // Services
  private hotkeys = inject(HotkeysService);
  private destroyRef = inject(DestroyRef);

  constructor() {
    this.setupHotkeys();

    // 1. Keep internal state in sync with external value input
    effect(() => {
      this.inputValue.set(this.value());
    });

    // 2. React to input changes and emit query for suggestions
    toObservable(this.inputValue).pipe(
      takeUntilDestroyed(),
      debounceTime(this.debounce()),
      distinctUntilChanged()
    ).subscribe(val => {
      const currentQuery = this.multi() ? this.getLastWord(val) : val;
      if (currentQuery.length > 0) {
        this.internalLoading.set(true);
      }
      this.queryChange.emit(currentQuery);
      this.selectedIndex.set(-1);
    });

    // 3. Auto-manage dropdown open state logic and Loading reset
    effect(() => {
      const suggestions = this.suggestions();
      const hasSuggestions = suggestions.length > 0;
      const hasFocus = document.activeElement === this.inputElement()?.nativeElement;

      // When new suggestions arrive, stop loading
      untracked(() => this.internalLoading.set(false));

      if (hasSuggestions && hasFocus && !this.manualClose()) {
        this.isDropdownOpen.set(true);
      } else if (!hasSuggestions || !hasFocus) {
        this.manualClose.set(false);
        this.isDropdownOpen.set(false);
      }
    });

    // 4. Blur input when user scrolls on mobile (dismiss keyboard)
    fromEvent(window, 'touchmove', { passive: true }).pipe(
      takeUntilDestroyed()
    ).subscribe(() => {
      if (document.activeElement === this.inputElement()?.nativeElement) {
        this.blurInput();
        this.closeDropdown();
      }
    });
  }

  private getLastWord(text: string): string {
    const parts = text.split(/\s+/);
    return parts[parts.length - 1] || '';
  }

  onInput(event: Event) {
    const val = (event.target as HTMLInputElement).value;
    this.inputValue.set(val);
    this.valueChange.emit(val);
    this.manualClose.set(false);
  }

  onFocus() {
    if (this.suggestions().length > 0) {
      this.isDropdownOpen.set(true);
    }
  }

  onKeyDown(event: KeyboardEvent) {
    if (event.repeat) return;

    const isOpen = this.isDropdownOpen();

    if (!isOpen) {
      switch (event.key) {
        case 'Enter':
          this.executeSearch();
          break;
        case 'Escape':
          event.preventDefault();
          this.blurInput();
          break;
      }
      return;
    }

    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        this.selectedIndex.update(i => (i + 1) % this.suggestions().length);
        this.scrollSelectedIntoView();
        break;
      case 'ArrowUp':
        event.preventDefault();
        this.selectedIndex.update(i => (i <= 0 ? this.suggestions().length - 1 : i - 1));
        this.scrollSelectedIntoView();
        break;
      case 'Enter':
      case 'Tab': {
        const hasSelection = this.selectedIndex() >= 0;
        if (hasSelection) {
          event.preventDefault();
          this.selectItem(this.suggestions()[this.selectedIndex()]);
        } else if (event.key === 'Enter') {
          this.executeSearch();
        }
        break;
      }
      case 'Escape':
        event.preventDefault();
        this.closeDropdown();
        break;
    }

  }

  setSelectedIndex(index: number) {
    this.selectedIndex.set(index);
  }

  selectItem(item: T) {
    this.selection.emit(item);
    this.closeDropdown();
    // Force focus back to input so user can continue typing (multi-tag)
    this.focusInput();
  }

  executeSearch() {
    // If the dropdown is open and we have a selection, choose it first
    if (this.isDropdownOpen() && this.selectedIndex() >= 0) {
      this.selectItem(this.suggestions()[this.selectedIndex()]);
    } else {
      this.searchTrigger.emit(this.inputValue());
      this.closeDropdown();
    }
  }

  closeDropdown() {
    this.isDropdownOpen.set(false);
    this.selectedIndex.set(-1);
    this.manualClose.set(true);
  }

  clearInput() {
    this.inputValue.set('');
    this.valueChange.emit('');
    this.queryChange.emit('');
    this.searchTrigger.emit('');
    this.closeDropdown();
    this.focusInput();
  }

  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent) {
    const target = event.target as HTMLElement;
    if (!this.inputElement()?.nativeElement.contains(target)) {
      this.closeDropdown();
    }
  }

  private setupHotkeys() {
    toObservable(this.focusShortcut)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        filter((s): s is FocusShortcut => !!s),
        switchMap(s => this.hotkeys.on(s.key, {
          ctrl: s.modifier === 'ctrl',
          meta: s.modifier === 'meta',
          allowInInput: true,
          preventDefault: true
        }))
      )
      .subscribe(() => this.focusInput());
  }

  private optionItems = viewChildren<ElementRef>('optionItem');

  private scrollSelectedIntoView() {
    const index = this.selectedIndex();
    const items = this.optionItems();

    // We might still need a small tick if the list just rendered, but usually viewChildren handles it.
    // However, if we scroll *immediately* after setting index (which might be same tick as data update),
    // we want to ensure DOM is ready.
    requestAnimationFrame(() => {
      if (items[index]) {
        items[index].nativeElement.scrollIntoView({ block: 'nearest' });
      }
    });
  }

  private focusInput() {
    requestAnimationFrame(() => {
      this.inputElement()?.nativeElement.focus();
    });
  }

  private blurInput() {
    requestAnimationFrame(() => {
      this.inputElement()?.nativeElement.blur();
    });
  }
}
