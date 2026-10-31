import { ChangeDetectionStrategy, Component, DestroyRef, HostListener, computed, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { NgTemplateOutlet } from '@angular/common';
import { CdkMenu, CdkMenuItem, CdkMenuTrigger } from '@angular/cdk/menu';
import { NavigationEnd, Router, RouterLink, RouterLinkActive } from '@angular/router';
import { filter, map, startWith } from 'rxjs';

import { ButtonDirective, TooltipDirective } from '@shared/directives';

export interface NavbarLink {
  route: (string | number)[];
  label: string;
}

@Component({
  selector: 'app-navbar',
  standalone: true,
  imports: [NgTemplateOutlet, RouterLink, RouterLinkActive, CdkMenu, CdkMenuItem, CdkMenuTrigger, ButtonDirective, TooltipDirective],
  templateUrl: './navbar.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class NavbarComponent {
  /** Pages used day to day, always visible. */
  links = input<NavbarLink[]>([]);
  /** Admin and configuration pages, behind the Manage menu. */
  manageLinks = input<NavbarLink[]>([]);
  helpRoute = input<(string | number)[]>([]);
  settingsRoute = input<(string | number)[]>([]);
  currentUser = input<string | null | undefined>('');
  authEnabled = input<boolean>(true);

  logout = output<void>();
  drawerOpenChange = output<boolean>();

  readonly drawerOpen = signal(false);

  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  private readonly navigationEnd = toSignal(
    this.router.events.pipe(filter(event => event instanceof NavigationEnd), map(() => this.router.url), startWith(this.router.url)),
    { initialValue: this.router.url },
  );

  /** Highlights the Manage button while one of its pages is open. */
  readonly manageActive = computed(() => {
    this.navigationEnd();
    return this.manageLinks().some(link => this.router.isActive(this.router.createUrlTree(link.route), {
      paths: 'subset',
      queryParams: 'ignored',
      fragment: 'ignored',
      matrixParams: 'ignored',
    }));
  });

  constructor() {
    this.router.events
      .pipe(
        filter(event => event instanceof NavigationEnd),
        takeUntilDestroyed(this.destroyRef)
      )
      .subscribe(() => this.closeDrawer());
  }

  toggleDrawer(): void {
    this.setDrawerOpen(!this.drawerOpen());
  }

  closeDrawer(): void {
    this.setDrawerOpen(false);
  }

  onLogout(): void {
    this.logout.emit();
    this.closeDrawer();
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    this.closeDrawer();
  }

  private setDrawerOpen(open: boolean): void {
    if (this.drawerOpen() === open) {
      return;
    }

    this.drawerOpen.set(open);
    this.drawerOpenChange.emit(open);
  }
}
