import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { DecimalPipe } from '@angular/common';

import { TabComponent } from '@shared/components/tabs/tab.component';
import { TabsComponent } from '@shared/components/tabs/tabs.component';

import { InboxBulkComponent } from './inbox-bulk.component';
import { InboxReviewComponent } from './inbox-review.component';
import { InboxStore } from './inbox.store';

@Component({
  selector: 'app-inbox',
  standalone: true,
  imports: [DecimalPipe, TabsComponent, TabComponent, InboxReviewComponent, InboxBulkComponent],
  providers: [InboxStore],
  host: { class: 'flex flex-1 flex-col min-h-0' },
  templateUrl: './inbox.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class InboxComponent {
  readonly store = inject(InboxStore);

  constructor() {
    this.store.load();
  }
}
