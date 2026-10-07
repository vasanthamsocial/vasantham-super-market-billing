// Security and licence settings (the .NET API's SecurityOptions), from environment variables with the same defaults.

function number(name: string, fallback: number): number {
  const value = Number(process.env[name]);
  return Number.isFinite(value) && value > 0 ? value : fallback;
}

export const settings = {
  get secureCookies(): boolean {
    return process.env.SB_SECURE_COOKIES !== 'false';
  },
  get sessionIdleMinutes(): number {
    return number('SB_SESSION_IDLE_MINUTES', 30);
  },
  get sessionAbsoluteHours(): number {
    return number('SB_SESSION_ABSOLUTE_HOURS', 12);
  },
  get maxFailedLogins(): number {
    return number('SB_MAX_FAILED_LOGINS', 5);
  },
  get lockoutMinutes(): number {
    return number('SB_LOCKOUT_MINUTES', 15);
  },
  get passwordResetMinutes(): number {
    return number('SB_PASSWORD_RESET_MINUTES', 30);
  },
  get approvalLifetimeDays(): number {
    return number('SB_APPROVAL_LIFETIME_DAYS', 7);
  },
  get maxBusinesses(): number {
    return number('SB_MAX_BUSINESSES', 1);
  },
  get permitPerMinute(): number {
    return number('SB_RATE_LIMIT_PER_MINUTE', 600);
  },
  get authPermitPerMinute(): number {
    return number('SB_AUTH_RATE_LIMIT_PER_MINUTE', 10);
  },
  get archiveWebEnabled(): boolean {
    return process.env.ARCHIVE_WEB_ENABLED === 'true';
  },
  get environment(): string {
    return process.env.FUNCTIONS_EMULATOR === 'true' ? 'Development' : 'Production';
  },
};
