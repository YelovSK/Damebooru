import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, HostListener, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive } from '@angular/router';
import { filter } from 'rxjs';

import { ButtonDirective } from '@shared/directives';

export interface NavbarLink {
  route: (string | number)[];
  icon?: string;
  label: string;
}

@Component({
  selector: 'app-navbar',
  standalone: true,
  imports: [CommonModule, RouterLink, RouterLinkActive, ButtonDirective],
  templateUrl: './navbar.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class NavbarComponent {
  links = input<NavbarLink[]>([]);
  currentUser = input<string | null | undefined>('');
  authEnabled = input<boolean>(true);

  logout = output<void>();
  navigated = output<void>();
  drawerOpenChange = output<boolean>();

  readonly drawerOpen = signal(false);

  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

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

  onLinkNavigate(): void {
    this.navigated.emit();
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
