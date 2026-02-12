import { Component, ChangeDetectionStrategy, inject, signal } from '@angular/core';

import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { ButtonComponent } from '@shared/components/button/button.component';
import { BakabooruService } from '@services/api/bakabooru/bakabooru.service';
import { AppLinks } from '@app/app.paths';

@Component({
  selector: 'app-login',
  imports: [FormsModule, ButtonComponent],
  templateUrl: './login.component.html',
  styleUrl: './login.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class LoginComponent {
  private readonly bakabooru = inject(BakabooruService);
  private readonly router = inject(Router);

  username = '';
  password = '';
  loading = signal(false);
  error = signal('');

  onLogin() {
    if (!this.username || !this.password) return;

    this.loading.set(true);
    this.error.set('');

    this.bakabooru.login(this.username, this.password).subscribe({
      next: () => {
        this.router.navigate(AppLinks.home());
      },
      error: (err) => {
        console.error(err);
        this.error.set('Invalid username or password. Please try again.');
        this.loading.set(false);
      }
    });
  }
}
