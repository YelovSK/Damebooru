import { ChangeDetectionStrategy, Component, input, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { type ControlValueAccessor, NgControl, FormsModule } from '@angular/forms';

import { FormErrorsComponent } from '../form-errors/form-errors.component';
import { generateId } from '@shared/utils/utils';

@Component({
  selector: 'app-form-checkbox',
  standalone: true,
  imports: [CommonModule, FormsModule, FormErrorsComponent],
  templateUrl: './form-checkbox.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class FormCheckboxComponent implements ControlValueAccessor {
  // UI inputs
  label = input<string>();
  id = input<string>(generateId());

  // Internal state
  value = false;
  disabled = false;

  // eslint-disable-next-line @typescript-eslint/no-empty-function
  onChange: (value: boolean) => void = () => { };
  // eslint-disable-next-line @typescript-eslint/no-empty-function
  onTouched: () => void = () => { };

  public ngControl = inject(NgControl, { optional: true, self: true });

  constructor() {
    if (this.ngControl) {
      this.ngControl.valueAccessor = this;
    }
  }

  writeValue(value: boolean): void {
    this.value = !!value;
  }

  registerOnChange(fn: (value: boolean) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }

  setDisabledState(isDisabled: boolean): void {
    this.disabled = isDisabled;
  }

  toggle(): void {
    if (!this.disabled) {
      this.value = !this.value;
      this.onChange(this.value);
      this.onTouched();
    }
  }
}
