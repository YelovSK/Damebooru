import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  HostListener,
  computed,
  effect,
  forwardRef,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';

export interface FormDropdownOption<T = unknown> {
  label: string;
  value: T;
}

@Component({
  selector: 'app-form-dropdown',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './form-dropdown.component.html',
  styleUrl: './form-dropdown.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [
    {
      provide: NG_VALUE_ACCESSOR,
      useExisting: forwardRef(() => FormDropdownComponent),
      multi: true,
    },
  ],
})
export class FormDropdownComponent<T = unknown> implements ControlValueAccessor {
  private readonly elementRef = inject(ElementRef<HTMLElement>);

  options = input.required<FormDropdownOption<T>[]>();
  value = input<T | null | undefined>(undefined);
  placeholder = input<string>('Select an option');
  label = input<string>('');
  disabled = input<boolean>(false);

  valueChange = output<T | null>();

  isOpen = signal(false);
  private readonly internalValue = signal<T | null>(null);
  private readonly cvaDisabled = signal(false);

  readonly selectedOption = computed(
    () => this.options().find(option => Object.is(option.value, this.internalValue())) ?? null,
  );
  readonly isDisabled = computed(() => this.disabled() || this.cvaDisabled());

  private onChange: (value: T | null) => void = () => {};
  private onTouched: () => void = () => {};

  constructor() {
    effect(() => {
      const inputValue = this.value();
      if (inputValue !== undefined) {
        this.internalValue.set(inputValue);
      }
    });
  }

  toggle(): void {
    if (this.isDisabled()) {
      return;
    }

    this.isOpen.update(open => !open);
  }

  select(option: FormDropdownOption<T>): void {
    if (this.isDisabled()) {
      return;
    }

    this.setValue(option.value, true);
    this.isOpen.set(false);
    this.markTouched();
  }

  @HostListener('document:pointerdown', ['$event'])
  onDocumentPointerDown(event: PointerEvent): void {
    if (!this.elementRef.nativeElement.contains(event.target as Node)) {
      this.isOpen.set(false);
    }
  }

  @HostListener('document:keydown.escape')
  onEscapeKey(): void {
    this.isOpen.set(false);
  }

  writeValue(value: T | null): void {
    this.internalValue.set(value);
  }

  registerOnChange(fn: (value: T | null) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }

  setDisabledState(isDisabled: boolean): void {
    this.cvaDisabled.set(isDisabled);
    if (isDisabled) {
      this.isOpen.set(false);
    }
  }

  private setValue(value: T | null, emit: boolean): void {
    this.internalValue.set(value);
    if (!emit) {
      return;
    }

    this.onChange(value);
    this.valueChange.emit(value);
  }

  private markTouched(): void {
    this.onTouched();
  }
}
