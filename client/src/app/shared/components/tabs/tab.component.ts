import { Component, input, TemplateRef, contentChild, ChangeDetectionStrategy, EventEmitter, Output } from '@angular/core';

@Component({
  selector: 'app-tab',
  standalone: true,
  template: ``,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class TabComponent {
  /** Route segment for this tab (e.g., 'auto-tagging') */
  id = input.required<string>();

  /** Display label for the tab */
  label = input.required<string>();

  /** Optional icon class */
  icon = input<string>();

  /** Template content for this tab */
  content = contentChild(TemplateRef);

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
