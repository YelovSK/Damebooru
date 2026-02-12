import { HttpInterceptorFn, HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { ToastService } from '@services/toast.service';

// I think this is the Oxibooru API error response format.
interface ErrorResponse {
    title: string;
    name: string;
    description: string;
}


export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  const toastService = inject(ToastService);

  return next(req).pipe(
    catchError((error: HttpErrorResponse) => {
      const isSessionProbe = req.url.includes('/auth/me');
      if (isSessionProbe && error.status === 401) {
        return throwError(() => error);
      }

      let errorMessage = 'An unexpected error occurred';

      if (error.error instanceof ErrorEvent) {
        // Client-side error
        errorMessage = error.error.message;
      } else {
        // Server-side error
        console.error('API Error:', error);
        
        if (error.status === 401) {
          errorMessage = 'Unauthorized. Please login again.';
        } else if (error.error && typeof error.error === 'string') {
          errorMessage = error.error;
        } else if (error.error && error.error.description) {
          errorMessage = error.error.description;
        } else if (error.error && error.error.title) {
          errorMessage = error.error.title;
        } else if (error.message) {
          errorMessage = error.message;
        }
      }

      toastService.error(errorMessage);
      return throwError(() => error);
    })
  );
};
