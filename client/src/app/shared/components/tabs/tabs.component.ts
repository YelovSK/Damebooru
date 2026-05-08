import { booleanAttribute, ChangeDetectionStrategy, Component, DestroyRef, type ElementRef, HostListener, contentChildren, computed, effect, inject, input, signal, viewChild, viewChildren } from '@angular/core';
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
  host: {
    class: 'flex flex-col flex-1 min-h-0',
  },
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class TabsComponent {
  private readonly router = inject(Router);
  private readonly activatedRoute = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  route = input(false, { transform: booleanAttribute });
  tabs = contentChildren(TabComponent);
  private readonly tabList = viewChild<ElementRef<HTMLElement>>('tabList');
  private readonly tabControls = viewChildren<ElementRef<HTMLElement>>('tabControl');
  private readonly localActiveTabId = signal('');
  readonly indicatorWidth = signal(0);
  readonly indicatorTransform = signal('translateX(0px)');
  readonly indicatorVisible = signal(false);
  private lastActivatedTabId = '';
  private indicatorFrame: number | null = null;

  private currentUrl = toSignal(
    this.router.events.pipe(
      filter(event => event instanceof NavigationEnd),
      map(() => this.router.url),
      startWith(this.router.url)
    ),
    { initialValue: this.router.url }
  );

  basePath = computed(() => {
    const url = this.currentUrl().split('?')[0];
    const segments = url.split('/').filter(s => s);
    const allTabs = this.tabs();
    const lastSegment = segments[segments.length - 1];
    
    // If last segment is a tab id and there is a parent segment,
    // base path is everything before the tab segment.
    // This avoids collapsing routes like "/tags" to "/" when a tab id is also "tags".
    if (allTabs.some(t => t.id() === lastSegment) && segments.length > 1) {
      return '/' + segments.slice(0, -1).join('/');
    }
    return '/' + segments.join('/');
  });

  private routeActiveTabId = computed(() => {
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

  activeTabId = computed(() => this.route() ? this.routeActiveTabId() : this.localActiveTabId());

  activeTab = computed(() => {
    const activeId = this.activeTabId();
    const allTabs = this.visibleTabs();
    return allTabs.find(t => t.id() === activeId) || allTabs[0];
  });

  visibleTabs = computed(() => this.tabs().filter(tab => !tab.hidden()));

  constructor() {
    effect(() => {
      const allTabs = this.visibleTabs();
      const activeId = this.activeTabId();
      
      if (allTabs.length > 0 && !activeId) {
        const firstTab = allTabs[0];
        if (this.route()) {
          this.router.navigate([firstTab.id()], { relativeTo: this.activatedRoute, replaceUrl: true });
        } else {
          this.localActiveTabId.set(firstTab.id());
        }
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

    effect(() => {
      this.activeTabId();
      this.visibleTabs();
      this.tabControls();
      this.updateIndicatorSoon();
    });

    this.destroyRef.onDestroy(() => {
      if (this.indicatorFrame !== null) {
        cancelAnimationFrame(this.indicatorFrame);
      }
    });
  }

  selectTab(tabId: string): void {
    if (this.route()) {
      this.router.navigate([this.basePath(), tabId], { replaceUrl: true });
      return;
    }

    this.localActiveTabId.set(tabId);
  }

  @HostListener('window:resize')
  handleResize(): void {
    this.updateIndicatorSoon();
  }

  private updateIndicatorSoon(): void {
    if (this.indicatorFrame !== null) {
      cancelAnimationFrame(this.indicatorFrame);
    }

    this.indicatorFrame = requestAnimationFrame(() => {
      this.indicatorFrame = null;
      this.updateIndicator();
    });
  }

  private updateIndicator(): void {
    const active = this.activeTab();
    const list = this.tabList()?.nativeElement;
    if (!active || !list) {
      this.indicatorVisible.set(false);
      return;
    }

    const activeIndex = this.visibleTabs().findIndex(tab => tab === active);
    const activeControl = this.tabControls()[activeIndex]?.nativeElement;
    if (!activeControl) {
      this.indicatorVisible.set(false);
      return;
    }

    const listRect = list.getBoundingClientRect();
    const activeRect = activeControl.getBoundingClientRect();

    this.indicatorWidth.set(activeRect.width);
    this.indicatorTransform.set(`translateX(${activeRect.left - listRect.left + list.scrollLeft}px)`);
    this.indicatorVisible.set(true);
  }
}
