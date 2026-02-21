import { HttpInterceptorFn } from "@angular/common/http";
import { inject } from "@angular/core";
import { DamebooruService } from "./damebooru/damebooru.service";
import { environment } from "@env/environment";

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const damebooru = inject(DamebooruService);
  const auth = damebooru.authHeader();

  // Only add auth header to internal API requests
  const isInternalApi = req.url.startsWith(environment.apiBaseUrl);

  if (isInternalApi) {
    const setHeaders =
      auth && !req.headers.has("Authorization")
        ? { Authorization: auth }
        : undefined;

    req = req.clone({
      withCredentials: true,
      setHeaders,
    });
  }

  return next(req);
};
