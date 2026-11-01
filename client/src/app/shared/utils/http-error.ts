import { HttpErrorResponse } from '@angular/common/http';

/** `failure` plus the server's reason when it sent one, e.g. "Failed to rename library: Name is taken." */
export function errorMessage(failure: string, error: unknown): string {
  const reason = serverReason(error);
  return reason ? `${failure}: ${reason}` : failure;
}

// The API sends plain-text messages for failed results and ProblemDetails for validation errors.
function serverReason(error: unknown): string | null {
  if (!(error instanceof HttpErrorResponse)) {
    return null;
  }

  const body: unknown = error.error;
  if (typeof body === 'string') {
    const text = body.trim();
    return text && !text.startsWith('<') ? text : null;
  }

  if (typeof body === 'object' && body !== null) {
    const { detail, title } = body as { detail?: unknown; title?: unknown };
    if (typeof detail === 'string' && detail.trim()) return detail.trim();
    if (typeof title === 'string' && title.trim()) return title.trim();
  }

  return null;
}
