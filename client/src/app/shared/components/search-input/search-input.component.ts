import { ChangeDetectionStrategy, Component, DestroyRef, inject, input, output, signal } from '@angular/core';
import { toObservable, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { debounceTime, distinctUntilChanged, skip } from 'rxjs';

@Component({
  selector: 'app-search-input',
  standalone: true,
  templateUrl: './search-input.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class SearchInputComponent {
  private readonly destroyRef = inject(DestroyRef);

  label = input('');
  placeholder = input('Search...');
  debounceMs = input(250);
  value = input('');

  search = output<string>();

  query = signal('');

  constructor() {
    toObservable(this.value)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(v => this.query.set(v));

    toObservable(this.query)
      .pipe(
        // Do not emit on initial render; parent pages usually perform an explicit first load.
        skip(1),
        debounceTime(this.debounceMs()),
        distinctUntilChanged(),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(v => this.search.emit(v.trim()));
  }

  onInput(event: Event): void {
    this.query.set((event.target as HTMLInputElement).value);
  }

  clear(): void {
    this.query.set('');
    this.search.emit('');
  }
}
