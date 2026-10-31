import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Router, RouterOutlet } from '@angular/router';

import { DamebooruService } from '@services/api/damebooru/damebooru.service';
import { ToastsComponent } from '@shared/components/toasts/toasts.component';
import { ConfirmDialogComponent } from '@shared/components/confirm-dialog/confirm-dialog.component';
import { NavbarComponent, type NavbarLink } from '@shared/components/navbar/navbar.component';
import { AppLinks } from '@app/app.paths';

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

  readonly navLinks: NavbarLink[] = [
    { route: AppLinks.posts(), icon: 'icon-posts', label: 'Posts' },
    { route: AppLinks.inbox(), icon: 'icon-posts', label: 'Inbox' },
    { route: AppLinks.libraries(), icon: 'icon-settings', label: 'Libraries' },
    { route: AppLinks.tags(), icon: 'icon-settings', label: 'Tags' },
    { route: AppLinks.jobs(), icon: 'icon-settings', label: 'Jobs' },
    { route: AppLinks.logs(), icon: 'icon-settings', label: 'Logs' },
    { route: AppLinks.duplicates(), icon: 'icon-settings', label: 'Duplicates' },
    { route: AppLinks.settings(), icon: 'icon-settings', label: 'Settings' },
    { route: AppLinks.stats(), icon: 'icon-info', label: 'Stats' },
    { route: AppLinks.help(), icon: 'icon-info', label: 'Help' },
  ];

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
