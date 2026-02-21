import { Component, ChangeDetectionStrategy, OnInit, inject, signal } from '@angular/core';

import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { ButtonComponent } from '@shared/components/button/button.component';
import { DamebooruService } from '@services/api/damebooru/damebooru.service';
import { AppLinks } from '@app/app.paths';

@Component({
  selector: 'app-login',
  imports: [FormsModule, ButtonComponent],
  templateUrl: './login.component.html',
  styleUrl: './login.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class LoginComponent implements OnInit {
  private readonly damebooru = inject(DamebooruService);
  private readonly router = inject(Router);

  username = '';
  password = '';
  loading = signal(false);
  error = signal('');

  ngOnInit() {
    this.damebooru.ensureAuthState().subscribe(isLoggedIn => {
      if (isLoggedIn) {
        this.router.navigate(AppLinks.posts());
      }
    });
  }

  onLogin() {
    if (!this.username || !this.password) return;

    this.loading.set(true);
    this.error.set('');

    this.damebooru.login(this.username, this.password).subscribe({
      next: () => {
        this.router.navigate(AppLinks.posts());
      },
      error: (err) => {
        console.error(err);
        this.error.set('Invalid username or password. Please try again.');
        this.loading.set(false);
      }
    });
  }
}
