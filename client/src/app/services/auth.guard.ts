import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { BakabooruService } from '@services/api/bakabooru/bakabooru.service';
import { AppPaths } from '@app/app.paths';
import { map } from 'rxjs';

export const authGuard: CanActivateFn = (route, state) => {
  const bakabooru = inject(BakabooruService);
  const router = inject(Router);

  return bakabooru.ensureAuthState().pipe(
    map(isLoggedIn => isLoggedIn ? true : router.parseUrl(`/${AppPaths.login}`))
  );
};
