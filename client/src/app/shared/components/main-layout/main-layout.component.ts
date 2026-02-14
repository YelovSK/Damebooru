import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, NavigationEnd, Router, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';

import { BakabooruService } from '@services/api/bakabooru/bakabooru.service';
import { ToastsComponent } from '@shared/components/toasts/toasts.component';
import { ConfirmDialogComponent } from '@shared/components/confirm-dialog/confirm-dialog.component';
import { NavbarComponent, NavbarLink } from '@shared/components/navbar/navbar.component';
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
  private readonly bakabooru = inject(BakabooruService);
  private readonly router = inject(Router);
  private readonly activatedRoute = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly pageWidth = signal<PageWidth>('content');

  readonly navLinks: NavbarLink[] = [
    { route: AppLinks.posts(), icon: 'icon-posts', label: 'Posts' },
    { route: AppLinks.libraries(), icon: 'icon-settings', label: 'Libraries' },
    { route: AppLinks.bulkTagging(), icon: 'icon-settings', label: 'Bulk-Tagging' },
    { route: AppLinks.tags(), icon: 'icon-settings', label: 'Tags' },
    { route: AppLinks.jobs(), icon: 'icon-settings', label: 'Jobs' },
    { route: AppLinks.duplicates(), icon: 'icon-settings', label: 'Duplicates' },
    { route: AppLinks.settings(), icon: 'icon-settings', label: 'Settings' },
    { route: AppLinks.info(), icon: 'icon-info', label: 'Info' },
    { route: AppLinks.help(), icon: 'icon-info', label: 'Help' },
  ];

  readonly pageContainerClasses = computed(() => {
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

  constructor() {
    this.router.events
      .pipe(
        filter(event => event instanceof NavigationEnd),
        takeUntilDestroyed(this.destroyRef)
      )
      .subscribe(() => this.pageWidth.set(this.resolvePageWidth()));

    this.pageWidth.set(this.resolvePageWidth());
  }

  get currentUser() {
    return this.bakabooru.currentUser();
  }

  private resolvePageWidth(): PageWidth {
    let route = this.activatedRoute.firstChild;

    while (route?.firstChild) {
      route = route.firstChild;
    }

    const value = route?.snapshot?.data?.['pageWidth'];
    if (value === 'full' || value === 'wide' || value === 'content') {
      return value;
    }

    return 'content';
  }

  onLogout() {
    this.bakabooru.logout().subscribe({
      next: () => this.router.navigate(AppLinks.login()),
      error: () => this.router.navigate(AppLinks.login())
    });
  }
}
