// Server-side forwarding of /api/* and /health/* from a web app to the SupermarketBilling API.
// API_INTERNAL_URL is read on every request (not at build time), so an installed release can be pointed at
// the store server's API without rebuilding.

const HOP_BY_HOP = new Set([
  'connection',
  'keep-alive',
  'proxy-authenticate',
  'proxy-authorization',
  'te',
  'trailer',
  'transfer-encoding',
  'upgrade',
  'host',
  'content-length',
]);

function apiBaseUrl(): string {
  return (process.env.API_INTERNAL_URL ?? 'http://localhost:5080').replace(/\/+$/, '');
}

async function forward(request: Request): Promise<Response> {
  const incoming = new URL(request.url);
  // Appended, not resolved: the API may live under a path (a Firebase function is .../asia-south1/api).
  const target = new URL(apiBaseUrl() + incoming.pathname + incoming.search);

  const headers = new Headers();
  request.headers.forEach((value, key) => {
    if (!HOP_BY_HOP.has(key)) headers.set(key, value);
  });
  // The API trusts X-Forwarded-* only from loopback proxies, so per-client rate limits keep working.
  headers.set('x-forwarded-host', incoming.host);
  headers.set('x-forwarded-proto', incoming.protocol.replace(':', ''));

  const init: RequestInit & { duplex?: 'half' } = {
    method: request.method,
    headers,
    redirect: 'manual',
    cache: 'no-store',
  };
  if (request.method !== 'GET' && request.method !== 'HEAD') {
    init.body = request.body;
    init.duplex = 'half';
  }

  let upstream: Response;
  try {
    upstream = await fetch(target, init);
  } catch {
    return Response.json(
      { title: 'API unreachable', status: 502, detail: 'The web server could not reach the SupermarketBilling API.' },
      { status: 502, headers: { 'cache-control': 'no-store' } },
    );
  }

  const responseHeaders = new Headers();
  upstream.headers.forEach((value, key) => {
    // fetch() has already decoded the body, so the original encoding and length no longer apply.
    if (!HOP_BY_HOP.has(key) && key !== 'content-encoding' && key !== 'set-cookie') {
      responseHeaders.set(key, value);
    }
  });
  for (const cookie of upstream.headers.getSetCookie()) {
    responseHeaders.append('set-cookie', cookie);
  }

  return new Response(upstream.body, { status: upstream.status, statusText: upstream.statusText, headers: responseHeaders });
}

export const GET = forward;
export const POST = forward;
export const PUT = forward;
export const PATCH = forward;
export const DELETE = forward;
