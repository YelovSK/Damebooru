import { Component, OnInit, ChangeDetectionStrategy, inject } from '@angular/core';

import { Router } from '@angular/router';
import { BakabooruService } from '@services/api/bakabooru/bakabooru.service';
import { ButtonComponent } from '@shared/components/button/button.component';
import { AppLinks } from '@app/app.paths';

@Component({
  selector: 'app-home',
  imports: [ButtonComponent],
  templateUrl: './home.component.html',
  styleUrl: './home.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class HomeComponent implements OnInit {
  private readonly bakabooru = inject(BakabooruService);
  private readonly router = inject(Router);

  ngOnInit() {
    this.bakabooru.ensureAuthState().subscribe(isLoggedIn => {
      if (isLoggedIn) {
        this.router.navigate(AppLinks.posts());
      }
    });
  }

  goToLogin() {
    this.router.navigate(AppLinks.login());
  }
}
