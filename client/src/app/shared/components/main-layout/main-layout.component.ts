import { Component, inject, ChangeDetectionStrategy } from '@angular/core';

import { RouterOutlet, Router, RouterLink, RouterLinkActive } from '@angular/router';
import { BakabooruService } from '@services/api/bakabooru/bakabooru.service';
import { ToastsComponent } from '@shared/components/toasts/toasts.component';
import { ButtonComponent } from '@shared/components/button/button.component';
import { ConfirmDialogComponent } from '@shared/components/confirm-dialog/confirm-dialog.component';
import { AppLinks } from '@app/app.paths';

@Component({
  selector: 'app-main-layout',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ToastsComponent, ButtonComponent, ConfirmDialogComponent],
  templateUrl: './main-layout.component.html',
  styleUrl: './main-layout.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class MainLayoutComponent {
  private readonly bakabooru = inject(BakabooruService);
  private readonly router = inject(Router);
  readonly appLinks = AppLinks;

  get currentUser() {
    return this.bakabooru.currentUser();
  }

  onLogout() {
    this.bakabooru.logout().subscribe({
      next: () => this.router.navigate(AppLinks.login()),
      error: () => this.router.navigate(AppLinks.login())
    });
  }
}
