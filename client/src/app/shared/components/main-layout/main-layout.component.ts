import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationCancel, NavigationEnd, NavigationError, NavigationSkipped, NavigationStart, Router, RouterOutlet } from '@angular/router';
import { filter, map, of, switchMap, timer } from 'rxjs';

import { DamebooruService } from '@services/api/damebooru/damebooru.service';
import { ToastsComponent } from '@shared/components/toasts/toasts.component';
import { ConfirmDialogComponent } from '@shared/components/confirm-dialog/confirm-dialog.component';
import { NavbarComponent, type NavbarLink } from '@shared/components/navbar/navbar.component';
import { AppLinks } from '@app/app.paths';

// Fast navigations finish before the indicator would show, so they swap pages without any flash.
const NAVIGATION_INDICATOR_DELAY_MS = 200;

@Component({
  selector: 'app-main-layout',
  imports: [RouterOutlet, ToastsComponent, ConfirmDialogComponent, NavbarComponent],
  templateUrl: './main-layout.component.html',
  styleUrl: './main-layout.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class MainLayoutComponent {
  private readonly damebooru = inject(DamebooruService);
  private readonly router = inject(Router);

  readonly mobileNavOpen = signal(false);

  readonly navigating = toSignal(
    this.router.events.pipe(
      filter(event => event instanceof NavigationStart
        || event instanceof NavigationEnd
        || event instanceof NavigationCancel
        || event instanceof NavigationError
        || event instanceof NavigationSkipped),
      switchMap(event => event instanceof NavigationStart
        ? timer(NAVIGATION_INDICATOR_DELAY_MS).pipe(map(() => true))
        : of(false)),
    ),
    { initialValue: false },
  );

  readonly navLinks: NavbarLink[] = [
    { route: AppLinks.posts(), label: 'Posts' },
    { route: AppLinks.inbox(), label: 'Inbox' },
    { route: AppLinks.tags(), label: 'Tags' },
    { route: AppLinks.duplicates(), label: 'Duplicates' },
    { route: AppLinks.stats(), label: 'Stats' },
  ];

  readonly manageLinks: NavbarLink[] = [
    { route: AppLinks.libraries(), label: 'Libraries' },
    { route: AppLinks.jobs(), label: 'Jobs' },
    { route: AppLinks.logs(), label: 'Logs' },
  ];

  readonly helpRoute = AppLinks.help();
  readonly settingsRoute = AppLinks.settings();

  get currentUser() {
    return this.damebooru.currentUser();
  }

  get authEnabled() {
    return this.damebooru.authEnabled();
  }

  onLogout() {
    this.damebooru.logout().subscribe({
      next: () => this.router.navigate(AppLinks.login()),
      error: () => this.router.navigate(AppLinks.login())
    });
  }
}
