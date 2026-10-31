import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, NavigationEnd, Router, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';

import { DamebooruService } from '@services/api/damebooru/damebooru.service';
import { ToastsComponent } from '@shared/components/toasts/toasts.component';
import { ConfirmDialogComponent } from '@shared/components/confirm-dialog/confirm-dialog.component';
import { NavbarComponent, type NavbarLink } from '@shared/components/navbar/navbar.component';
import { AppLinks } from '@app/app.paths';

type PageWidth = 'full' | 'wide' | 'content';

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
  private readonly activatedRoute = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly pageWidth = signal<PageWidth>('content');
  /** Pages that lay themselves out in exactly the visible height (route data `fillHeight`) and scroll their own parts. */
  readonly fillHeight = signal(false);
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

  private readonly pageWidthClasses = computed(() => {
    switch (this.pageWidth()) {
      case 'full':
        return 'w-full max-w-none';
      case 'wide':
        return 'mx-auto w-full max-w-[1400px]';
      case 'content':
      default:
        return 'mx-auto w-full max-w-6xl';
    }
  });
  readonly pageContainerClasses = computed(() => this.pageWidthClasses() + (this.fillHeight() ? ' flex h-full flex-col' : ''));

  constructor() {
    this.router.events
      .pipe(
        filter(event => event instanceof NavigationEnd),
        takeUntilDestroyed(this.destroyRef)
      )
      .subscribe(() => this.applyRouteLayout());

    this.applyRouteLayout();
  }

  get currentUser() {
    return this.damebooru.currentUser();
  }

  get authEnabled() {
    return this.damebooru.authEnabled();
  }

  private applyRouteLayout(): void {
    let route = this.activatedRoute.firstChild;

    while (route?.firstChild) {
      route = route.firstChild;
    }

    const data = route?.snapshot?.data;
    const width = data?.['pageWidth'];
    this.pageWidth.set(width === 'full' || width === 'wide' || width === 'content' ? width : 'content');
    this.fillHeight.set(data?.['fillHeight'] === true);
  }

  onLogout() {
    this.damebooru.logout().subscribe({
      next: () => this.router.navigate(AppLinks.login()),
      error: () => this.router.navigate(AppLinks.login())
    });
  }
}
