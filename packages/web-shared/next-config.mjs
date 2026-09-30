// Shared Next.js configuration for every SupermarketBilling web application.
// Plain JavaScript so next.config.ts can import it without a build step.

const isDev = process.env.NODE_ENV !== 'production';

// Everything is served from this origin: no CDNs, online fonts or third-party scripts (offline-first LAN use).
// TODO(stage 15): replace 'unsafe-inline' scripts with per-request nonces.
const contentSecurityPolicy = [
  "default-src 'self'",
  `script-src 'self' 'unsafe-inline'${isDev ? " 'unsafe-eval'" : ''}`,
  "style-src 'self' 'unsafe-inline'",
  "img-src 'self' data: blob:",
  "font-src 'self'",
  `connect-src 'self'${isDev ? ' ws:' : ''}`,
  "object-src 'none'",
  "base-uri 'self'",
  "form-action 'self'",
  "frame-ancestors 'none'",
].join('; ');

/**
 * @param {{ appName: string }} options
 * @returns {import('next').NextConfig}
 */
export function createNextConfig({ appName }) {
  return {
    poweredByHeader: false,
    // End-to-end tests run their own servers alongside development ones, so they build into a separate folder.
    distDir: process.env.NEXT_DIST_DIR || '.next',
    reactStrictMode: true,
    // Self-contained server output for offline installation on the store server.
    output: 'standalone',
    transpilePackages: ['@sb/web-shared'],
    env: {
      NEXT_PUBLIC_APP_NAME: appName,
    },
    // The browser only ever talks to its own origin. /api/* and /health/* are forwarded to the API by the
    // route handlers in app/api and app/health (see @sb/web-shared/api-proxy), which read API_INTERNAL_URL
    // at runtime. This keeps session cookies first-party and HTTP-only and avoids CORS entirely.
    async headers() {
      return [
        {
          source: '/:path*',
          headers: [
            { key: 'Content-Security-Policy', value: contentSecurityPolicy },
            { key: 'X-Content-Type-Options', value: 'nosniff' },
            { key: 'X-Frame-Options', value: 'DENY' },
            { key: 'Referrer-Policy', value: 'no-referrer' },
            { key: 'Permissions-Policy', value: 'camera=(), microphone=(), geolocation=(), payment=()' },
            { key: 'Cross-Origin-Opener-Policy', value: 'same-origin' },
          ],
        },
      ];
    },
  };
}
