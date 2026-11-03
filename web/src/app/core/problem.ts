import { HttpErrorResponse } from '@angular/common/http';
import { AbstractControl, FormGroup } from '@angular/forms';

/** The API's RFC 7807 error body. */
export interface ProblemDetails {
  title?: string;
  status?: number;
  code?: string;
  errors?: Record<string, string[]>;
}

/** A readable message for any failed request. */
export function errorMessage(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    const problem = error.error as ProblemDetails | null;
    if (error.status === 0) return 'Cannot reach the server. Check your connection and try again.';
    if (error.status === 429) return 'Too many attempts. Please wait a minute and try again.';
    if (problem?.title) return problem.title;
  }
  return 'Something went wrong. Please try again.';
}

/**
 * Copies the API's field errors (400 validation responses) onto the matching form controls,
 * so they show under each field. Returns true when at least one field error was applied.
 */
export function applyServerErrors(form: FormGroup, error: unknown): boolean {
  if (!(error instanceof HttpErrorResponse) || error.status !== 400) return false;
  const errors = (error.error as ProblemDetails | null)?.errors ?? {};
  let applied = false;
  for (const [field, messages] of Object.entries(errors)) {
    const control: AbstractControl | null = form.get(field);
    if (control) {
      control.setErrors({ server: messages[0] });
      control.markAsTouched();
      applied = true;
    }
  }
  return applied;
}
