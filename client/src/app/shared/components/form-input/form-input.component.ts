import { ChangeDetectionStrategy, Component, input, computed, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { type ControlValueAccessor, NgControl, FormsModule } from '@angular/forms';

import { FormErrorsComponent } from '../form-errors/form-errors.component';
import { generateId } from '@shared/utils/utils';

@Component({
  selector: 'app-form-input',
  standalone: true,
  imports: [CommonModule, FormsModule, FormErrorsComponent],
  templateUrl: './form-input.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class FormInputComponent implements ControlValueAccessor {
  // UI inputs
  label = input<string>();
  placeholder = input<string>('');
  type = input<'text' | 'password' | 'email' | 'url'>('text');
  id = input<string>(generateId());

  // Internal state
  value = '';
  disabled = false;

  // eslint-disable-next-line @typescript-eslint/no-empty-function
  onChange: (value: string) => void = () => { };
  // eslint-disable-next-line @typescript-eslint/no-empty-function
  onTouched: () => void = () => { };

  public ngControl = inject(NgControl, { optional: true, self: true });

  constructor() {
    if (this.ngControl) {
      this.ngControl.valueAccessor = this;
    }
  }

  hasError = computed(() => !!(this.ngControl?.errors && this.ngControl.touched));

  writeValue(value: string): void {
    this.value = value || '';
  }

  registerOnChange(fn: (value: string) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }

  setDisabledState(isDisabled: boolean): void {
    this.disabled = isDisabled;
  }

  handleInput(event: Event): void {
    const target = event.target as HTMLInputElement;
    this.value = target.value;
    this.onChange(this.value);
  }

  handleBlur(): void {
    this.onTouched();
  }
}
