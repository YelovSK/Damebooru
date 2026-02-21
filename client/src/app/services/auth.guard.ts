import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { DamebooruService } from '@services/api/damebooru/damebooru.service';
import { AppPaths } from '@app/app.paths';
import { map } from 'rxjs';

export const authGuard: CanActivateFn = (route, state) => {
  const damebooru = inject(DamebooruService);
  const router = inject(Router);

  return damebooru.ensureAuthState().pipe(
    map(isLoggedIn => isLoggedIn ? true : router.parseUrl(`/${AppPaths.login}`))
  );
};
