// Expected failures, turned into RFC 7807 problem responses with a stable machine-readable code (as the .NET API did).

export type ErrorKind = 'validation' | 'unauthorized' | 'forbidden' | 'not_found' | 'conflict' | 'locked' | 'too_many_requests';

const statusFor: Record<ErrorKind, number> = {
  validation: 400,
  unauthorized: 401,
  forbidden: 403,
  not_found: 404,
  conflict: 409,
  locked: 423,
  too_many_requests: 429,
};

/** A failure the user can act on: shown with its message, never a stack trace. */
export class AppError extends Error {
  constructor(
    readonly kind: ErrorKind,
    readonly code: string,
    message: string,
  ) {
    super(message);
    this.name = 'AppError';
  }

  get status(): number {
    return statusFor[this.kind];
  }

  static validation(code: string, message: string): AppError {
    return new AppError('validation', code, message);
  }

  static forbidden(message = 'You do not have permission to do this.'): AppError {
    return new AppError('forbidden', 'forbidden', message);
  }

  static notFound(what: string): AppError {
    return new AppError('not_found', 'not_found', `${what} was not found.`);
  }

  static conflict(code: string, message: string): AppError {
    return new AppError('conflict', code, message);
  }
}

/** A broken business rule (the domain's own checks); reported as 400 with its code. */
export class DomainError extends Error {
  constructor(
    readonly code: string,
    message: string,
  ) {
    super(message);
    this.name = 'DomainError';
  }
}

const titles: Record<number, string> = {
  400: 'Bad Request',
  401: 'Unauthorized',
  403: 'Forbidden',
  404: 'Not Found',
  409: 'Conflict',
  413: 'Payload Too Large',
  423: 'Locked',
  429: 'Too Many Requests',
  500: 'An error occurred while processing your request.',
  503: 'Service Unavailable',
};

/** The problem body the web apps read: detail (shown), code (for decisions), title and status. */
export function problem(status: number, code: string, detail: string): Record<string, unknown> {
  return { type: `https://tools.ietf.org/html/rfc9110#section-15.${status >= 500 ? 6 : 5}`, title: titles[status] ?? 'Error', status, detail, code };
}
