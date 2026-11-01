import { HttpErrorResponse, type HttpInterceptorFn } from "@angular/common/http";
import { inject } from "@angular/core";
import { Router } from "@angular/router";
import { catchError, throwError } from "rxjs";
import { DamebooruService } from "./damebooru/damebooru.service";
import { environment } from "@env/environment";
import { AppLinks } from "@app/app.paths";

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const damebooru = inject(DamebooruService);
  const router = inject(Router);
  const auth = damebooru.authHeader();

  // Only add auth header to internal API requests
  const isInternalApi = req.url.startsWith(environment.apiBaseUrl);
  if (!isInternalApi) {
    return next(req);
  }

  const setHeaders =
    auth && !req.headers.has("Authorization")
      ? { Authorization: auth }
      : undefined;

  // Auth endpoints answer 401 as part of their normal flow (session check, wrong password).
  const isAuthEndpoint = req.url.startsWith(`${environment.apiBaseUrl}/auth/`);

  return next(req.clone({ withCredentials: true, setHeaders })).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && error.status === 401 && !isAuthEndpoint) {
        damebooru.currentUser.set(null);
        void router.navigate(AppLinks.login());
      }
      return throwError(() => error);
    }),
  );
};
