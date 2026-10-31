import { booleanAttribute, computed, Directive, input } from '@angular/core';

const baseClasses = 'h-10 w-full min-w-0 px-3 bg-sunken border rounded-lg text-sm text-fg outline-none transition-colors placeholder:text-fg-subtle disabled:opacity-50 disabled:cursor-not-allowed';
const validClasses = 'border-line hover:border-line-strong focus:border-accent';
const invalidClasses = 'border-danger';

@Directive({
  selector: 'input[appInput], textarea[appInput], button[appInput]',
  standalone: true,
  host: { '[class]': 'classes()' },
})
export class InputDirective {
  readonly invalid = input(false, { transform: booleanAttribute });

  protected readonly classes = computed(() => `${baseClasses} ${this.invalid() ? invalidClasses : validClasses}`);
}

@Directive({
  selector: '[appFieldLabel]',
  standalone: true,
  host: { class: 'block mb-1.5 text-sm font-medium text-fg-muted' },
})
export class FieldLabelDirective {}
