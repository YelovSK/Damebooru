import { ChangeDetectionStrategy, Component, input, computed, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { type ControlValueAccessor, NgControl, FormsModule } from '@angular/forms';

import { FormErrorsComponent } from '../form-errors/form-errors.component';
import { generateId } from '@shared/utils/utils';

@Component({
  selector: 'app-form-number-input',
  standalone: true,
  imports: [CommonModule, FormsModule, FormErrorsComponent],
  templateUrl: './form-number-input.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class FormNumberInputComponent implements ControlValueAccessor {
  // UI inputs
  label = input<string>();
  placeholder = input<string>('');
  id = input<string>(generateId());
  min = input<number | null>(null);
  max = input<number | null>(null);
  step = input<number>(1);

  // Internal state
  value: number | null = null;
  displayValue = '';
  disabled = false;

  // eslint-disable-next-line @typescript-eslint/no-empty-function
  onChange: (value: number | null) => void = () => { };
  // eslint-disable-next-line @typescript-eslint/no-empty-function
  onTouched: () => void = () => { };

  public ngControl = inject(NgControl, { optional: true, self: true });

  constructor() {
    if (this.ngControl) {
      this.ngControl.valueAccessor = this;
    }
  }

  hasError = computed(() => !!(this.ngControl?.errors && this.ngControl.touched));

  writeValue(value: number | null): void {
    this.value = value !== null && value !== undefined ? Number(value) : null;
    this.displayValue = this.formatValue(this.value);
  }

  registerOnChange(fn: (value: number | null) => void): void {
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
    this.displayValue = target.value;
  }

  handleBlur(): void {
    const next = this.parseDisplayValue();
    this.value = next;
    this.displayValue = this.formatValue(next);
    this.onChange(next);
    this.onTouched();
  }

  stepBy(direction: 1 | -1): void {
    if (this.disabled) {
      return;
    }

    const step = this.getValidStep();
    const baseline = this.value ?? this.min() ?? 0;
    const raw = baseline + direction * step;
    const clamped = this.clamp(raw);

    // Keep decimal stepping stable (e.g. 0.1 + 0.2).
    const next = Number(clamped.toFixed(this.getDecimalPlaces(step)));

    this.value = next;
    this.displayValue = this.formatValue(next);
    this.onChange(next);
    this.onTouched();
  }

  private parseDisplayValue(): number | null {
    const trimmed = this.displayValue.trim();
    if (trimmed.length === 0) {
      return null;
    }

    const parsed = Number(trimmed);
    if (!Number.isFinite(parsed)) {
      return this.value;
    }

    return this.clamp(parsed);
  }

  private formatValue(value: number | null): string {
    return value === null ? '' : String(value);
  }

  private clamp(value: number): number {
    const min = this.min();
    const max = this.max();

    if (min !== null && value < min) {
      return min;
    }

    if (max !== null && value > max) {
      return max;
    }

    return value;
  }

  private getValidStep(): number {
    const step = this.step();
    return Number.isFinite(step) && step > 0 ? step : 1;
  }

  private getDecimalPlaces(value: number): number {
    const text = value.toString();
    const dotIndex = text.indexOf('.');
    return dotIndex >= 0 ? text.length - dotIndex - 1 : 0;
  }
}
