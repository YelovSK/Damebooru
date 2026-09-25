import { ChangeDetectionStrategy, Component, ElementRef, HostListener, computed, inject, input, output, signal } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonDirective, type ButtonVariant } from '@shared/directives/button.directive';
import { TooltipDirective } from '@shared/directives/tooltip.directive';
import { FormDropdownComponent, type FormDropdownOption } from '@shared/components/dropdown/form-dropdown.component';
import { FormInputComponent } from '@shared/components/form-input/form-input.component';
import { FormNumberInputComponent } from '@shared/components/form-number-input/form-number-input.component';
import {
  NUMBER_OPERATORS,
  SEARCH_DIRECTIVES,
  SORT_DIRECTIONS,
  SORT_DIRECTIVE,
  applySearchDirective,
  clearSearchDirective,
  flipSearchDirective,
  getActiveSearchValues,
  parseNumberFilter,
  removeSearchDirective,
  type ActiveSearchValue,
  type NumberOperator,
  type SearchDirective,
} from '@shared/utils/post-search-syntax';

@Component({
  selector: 'app-post-search-filters',
  imports: [
    NgTemplateOutlet,
    FormsModule,
    ButtonDirective,
    TooltipDirective,
    FormDropdownComponent,
    FormInputComponent,
    FormNumberInputComponent,
  ],
  templateUrl: './post-search-filters.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'relative block' },
})
export class PostSearchFiltersComponent {
  private readonly elementRef = inject<ElementRef<HTMLElement>>(ElementRef);

  query = input.required<string>();
  /** `popover` floats next to the trigger; `inline` expands in place, for the mobile sheet. */
  layout = input<'popover' | 'inline'>('popover');

  queryChange = output<string>();
  searchTrigger = output<void>();

  readonly directives = SEARCH_DIRECTIVES;
  readonly operatorOptions: FormDropdownOption<NumberOperator>[] = NUMBER_OPERATORS.map(op => ({ label: op, value: op }));
  readonly sortDirectionOptions: FormDropdownOption<string>[] = SORT_DIRECTIONS.map(d => ({ label: d.label, value: d.value }));

  readonly open = signal(false);
  /** Pending text values per directive, added to the query on Add. */
  readonly drafts = signal<Readonly<Record<string, string>>>({});
  /** Operator to use for number directives that aren't in the query yet. */
  private readonly preferredOperators = signal<Readonly<Record<string, NumberOperator>>>({});

  readonly activeValues = computed(() => getActiveSearchValues(this.query()));

  readonly sortFieldOptions: FormDropdownOption<string>[] = SORT_DIRECTIVE.value.fields.map(f => ({ label: f.label, value: f.value }));
  private readonly sort = computed(() => {
    const active = this.activeValues().find(a => a.directive === SORT_DIRECTIVE)?.value;
    const [field, direction] = (active ?? SORT_DIRECTIVE.value.defaultSort).split(':');
    return { field, direction };
  });
  readonly sortField = computed(() => this.sort().field);
  readonly sortDirection = computed(() => this.sort().direction);

  toggle(): void {
    this.open.update(open => !open);
  }


  activeValuesOf(directive: SearchDirective): ActiveSearchValue[] {
    return this.activeValues().filter(a => a.directive === directive);
  }

  chipVariant(directive: SearchDirective, value: string): ButtonVariant {
    const active = this.findActive(directive, value);
    return !active ? 'secondary' : active.negated ? 'danger' : 'primary';
  }

  chipHint(directive: SearchDirective, value: string): string {
    const active = this.findActive(directive, value);
    if (!active) {
      return 'Click to include';
    }
    return directive.negatable && !active.negated ? 'Click to exclude' : 'Click to clear';
  }

  /** Cycles off → included → excluded (negatable directives only) → off. */
  toggleValue(directive: SearchDirective, value: string): void {
    const active = this.findActive(directive, value);
    if (!active) {
      this.add(directive, value, false);
    } else if (directive.negatable && !active.negated) {
      this.flip(active);
    } else {
      this.remove(active);
    }
  }

  setSortField(field: string | null): void {
    this.setSort(field ?? this.sortField(), this.sortDirection());
  }

  setSortDirection(direction: string | null): void {
    this.setSort(this.sortField(), direction ?? this.sortDirection());
  }

  draft(directive: SearchDirective): string {
    return this.drafts()[directive.key] ?? '';
  }

  setDraft(directive: SearchDirective, value: string): void {
    this.drafts.update(drafts => ({ ...drafts, [directive.key]: value }));
  }

  canAddDraft(directive: SearchDirective): boolean {
    const draft = this.draft(directive).trim();
    // The query is split on whitespace, so values can't contain it.
    return draft.length > 0 && !/\s/.test(draft);
  }

  addDraft(directive: SearchDirective): void {
    if (!this.canAddDraft(directive)) {
      return;
    }

    this.add(directive, this.draft(directive).trim());
    this.setDraft(directive, '');
  }

  operator(directive: SearchDirective): NumberOperator {
    return this.numberFilter(directive)?.operator ?? this.preferredOperators()[directive.key] ?? '>=';
  }

  numberValue(directive: SearchDirective): number | null {
    return this.numberFilter(directive)?.value ?? null;
  }

  // Number directives are single-valued, so the fields edit the query token directly; clearing the value removes it.
  setOperator(directive: SearchDirective, operator: NumberOperator | null): void {
    const next = operator ?? '>=';
    this.preferredOperators.update(operators => ({ ...operators, [directive.key]: next }));
    const value = this.numberValue(directive);
    if (value !== null) {
      this.add(directive, `${next}${value}`);
    }
  }

  setNumberValue(directive: SearchDirective, value: number | null): void {
    const active = this.activeValuesOf(directive)[0];
    if (value !== null) {
      this.add(directive, `${this.operator(directive)}${value}`);
    } else if (active) {
      this.remove(active);
    }
  }

  flip(active: ActiveSearchValue): void {
    this.queryChange.emit(flipSearchDirective(this.query(), active.directive, active.value, active.negated));
  }

  remove(active: ActiveSearchValue): void {
    this.queryChange.emit(removeSearchDirective(this.query(), active.directive, active.value, active.negated));
  }

  onSearch(): void {
    this.searchTrigger.emit();
    this.open.set(false);
  }

  @HostListener('document:pointerdown', ['$event'])
  onDocumentPointerDown(event: PointerEvent): void {
    if (this.layout() === 'popover' && !this.elementRef.nativeElement.contains(event.target as Node)) {
      this.open.set(false);
    }
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.layout() === 'popover') {
      this.open.set(false);
    }
  }

  private add(directive: SearchDirective, value: string, negated = false): void {
    this.queryChange.emit(applySearchDirective(this.query(), directive, value, negated));
  }

  private numberFilter(directive: SearchDirective) {
    const active = this.activeValuesOf(directive)[0];
    return active ? parseNumberFilter(active.value) : null;
  }

  /** The default order is expressed by having no sort token at all. */
  private setSort(field: string, direction: string): void {
    const value = `${field}:${direction}`;
    this.queryChange.emit(value === SORT_DIRECTIVE.value.defaultSort
      ? clearSearchDirective(this.query(), SORT_DIRECTIVE)
      : applySearchDirective(this.query(), SORT_DIRECTIVE, value));
  }

  private findActive(directive: SearchDirective, value: string): ActiveSearchValue | undefined {
    return this.activeValues().find(a => a.directive === directive && a.value === value);
  }
}
