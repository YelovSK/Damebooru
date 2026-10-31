import { ChangeDetectionStrategy, Component, effect, input, output, signal } from '@angular/core';

import { ButtonDirective, InputDirective, TooltipDirective } from '@shared/directives';

export interface LibraryPathItem {
  id: number;
  path: string;
}

@Component({
  selector: 'app-library-path-list',
  standalone: true,
  imports: [ButtonDirective, InputDirective, TooltipDirective],
  templateUrl: './library-path-list.component.html',
  host: { class: 'block min-w-0' },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LibraryPathListComponent {
  readonly heading = input.required<string>();
  readonly description = input.required<string>();
  readonly placeholder = input('');
  readonly paths = input.required<LibraryPathItem[]>();
  readonly adding = input(false);
  readonly removingId = input<number | null>(null);

  readonly add = output<string>();
  readonly remove = output<LibraryPathItem>();

  readonly draft = signal('');

  constructor() {
    // The parent reloads the list after a successful add; clearing only then keeps the text when the add fails.
    effect(() => {
      const draft = this.draft().trim();
      if (draft && this.paths().some(item => item.path === draft)) {
        this.draft.set('');
      }
    });
  }

  submit(): void {
    const path = this.draft().trim();
    if (path && !this.adding()) {
      this.add.emit(path);
    }
  }
}
