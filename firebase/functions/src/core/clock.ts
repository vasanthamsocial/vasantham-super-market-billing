// The time the backend works with. Tests can move it (as the .NET tests used a fake TimeProvider); production uses the real clock.

let offsetMs = 0;
let fixed: number | null = null;

export const clock = {
  now(): Date {
    return new Date(fixed ?? Date.now() + offsetMs);
  },
  /** Tests only. */
  set(at: Date | null): void {
    fixed = at ? at.getTime() : null;
  },
  /** Tests only. */
  advance(ms: number): void {
    if (fixed !== null) fixed += ms;
    else offsetMs += ms;
  },
};

export const DefaultTimeZone = 'Asia/Kolkata';

/** The business date (YYYY-MM-DD) of a moment in a time zone. */
export function businessDate(at: Date, timeZone = DefaultTimeZone): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit' }).format(at);
}
