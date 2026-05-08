import { ChangeDetectionStrategy, Component, EventEmitter, Output, TemplateRef, input, viewChild } from '@angular/core';

@Component({
  selector: 'app-tab',
  standalone: true,
  template: `
    <ng-template>
      <ng-content></ng-content>
    </ng-template>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class TabComponent {
  id = input.required<string>();
  label = input.required<string>();
  icon = input<string>();
  hidden = input(false);
  readonly content = viewChild(TemplateRef);

  /** Fires every time tab becomes active */
  @Output() open = new EventEmitter<void>();

  /** Fires only once, first time tab becomes active */
  @Output() init = new EventEmitter<void>();

  private hasInitialized = false;

  notifyActivated(): void {
    if (!this.hasInitialized) {
      this.hasInitialized = true;
      this.init.emit();
    }

    this.open.emit();
  }
}
