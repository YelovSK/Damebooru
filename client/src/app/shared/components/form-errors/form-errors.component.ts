import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { NgControl } from '@angular/forms';
import { toObservable } from '@angular/core/rxjs-interop';
import { EMPTY, merge, of } from 'rxjs';
import { map, shareReplay, startWith, switchMap } from 'rxjs/operators';

interface FormErrorsVm {
  show: boolean;
  errors: { key: string; value: unknown }[];
}

@Component({
  selector: 'app-form-errors',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './form-errors.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class FormErrorsComponent {
  control = input.required<NgControl | null>();
  label = input<string>();

  readonly vm$ = toObservable(this.control).pipe(
    switchMap(ngControl => {
      const abstractControl = ngControl?.control;
      if (!abstractControl) {
        return of<FormErrorsVm>({ show: false, errors: [] });
      }

      return merge(
        abstractControl.valueChanges,
        abstractControl.statusChanges,
        abstractControl.events ?? EMPTY
      ).pipe(
        startWith(null),
        map(() => {
          const errors = abstractControl.errors;
          return {
            show: !!(errors && abstractControl.touched),
            errors: Object.entries(errors ?? {}).map(([key, value]) => ({ key, value }))
          } satisfies FormErrorsVm;
        })
      );
    }),
    shareReplay({ bufferSize: 1, refCount: true })
  );
}
