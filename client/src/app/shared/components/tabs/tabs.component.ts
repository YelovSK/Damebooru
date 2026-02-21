import { Component, contentChildren, computed, ChangeDetectionStrategy, inject, effect } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterLink, RouterLinkActive, ActivatedRoute, NavigationEnd } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { filter, map, startWith } from 'rxjs';

import { TabComponent } from './tab.component';

@Component({
  selector: 'app-tabs',
  standalone: true,
  imports: [CommonModule, RouterLink, RouterLinkActive],
  templateUrl: './tabs.component.html',
  styleUrl: './tabs.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class TabsComponent {
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  /** Child tab components */
  tabs = contentChildren(TabComponent);
  private lastActivatedTabId = '';

  /** Current URL path */
  private currentUrl = toSignal(
    this.router.events.pipe(
      filter(event => event instanceof NavigationEnd),
      map(() => this.router.url),
      startWith(this.router.url)
    ),
    { initialValue: this.router.url }
  );

  /** Base path (parent route) */
  basePath = computed(() => {
    // Get the parent path by removing the last segment if it matches a tab
    const url = this.currentUrl().split('?')[0]; // Remove query params
    const segments = url.split('/').filter(s => s);
    const allTabs = this.tabs();
    const lastSegment = segments[segments.length - 1];
    
    // If last segment is a tab id and there is a parent segment,
    // base path is everything before the tab segment.
    // This avoids collapsing routes like "/tags" to "/" when a tab id is also "tags".
    if (allTabs.some(t => t.id() === lastSegment) && segments.length > 1) {
      return '/' + segments.slice(0, -1).join('/');
    }
    // Otherwise, current path is the base
    return '/' + segments.join('/');
  });

  /** Current active tab ID from route */
  activeTabId = computed(() => {
    const url = this.currentUrl().split('?')[0];
    const segments = url.split('/').filter(s => s);
    const lastSegment = segments[segments.length - 1];
    const allTabs = this.tabs();
    
    // Check if last segment matches a tab
    if (allTabs.some(t => t.id() === lastSegment)) {
      return lastSegment;
    }
    return '';
  });

  /** The currently active tab component */
  activeTab = computed(() => {
    const activeId = this.activeTabId();
    const allTabs = this.tabs();
    return allTabs.find(t => t.id() === activeId) || allTabs[0];
  });

  constructor() {
    // Redirect to first tab if none selected
    effect(() => {
      const allTabs = this.tabs();
      const activeId = this.activeTabId();
      
      if (allTabs.length > 0 && !activeId) {
        const firstTab = allTabs[0];
        this.router.navigate([firstTab.id()], { relativeTo: this.route, replaceUrl: true });
      }
    });

    effect(() => {
      const tab = this.activeTab();
      if (!tab) {
        return;
      }

      const tabId = tab.id();
      if (!tabId || this.lastActivatedTabId === tabId) {
        return;
      }

      this.lastActivatedTabId = tabId;
      tab.notifyActivated();
    });
  }
}
