import {
  Component,
  ChangeDetectionStrategy,
  input,
  model,
} from '@angular/core';

@Component({
  selector: 'app-collapsible',
  standalone: true,
  templateUrl: './collapsible.component.html',
  styleUrl: './collapsible.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CollapsibleComponent {
  /** The title displayed in the header */
  title = input.required<string>();

  /** Whether the collapsible is expanded */
  expanded = model<boolean>(false);

  toggle(): void {
    this.expanded.update(v => !v);
  }
}
