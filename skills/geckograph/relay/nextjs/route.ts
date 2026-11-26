import type { NextRequest } from "next/server";

/**
 * GeckoGraph widget relay for Next.js (App Router). Copy to `app/api/geckograph/[...path]/route.ts`
 * and point the widget at it: data-base-url="/api/geckograph" data-discovery-url="/api/geckograph".
 *
 * Needed when the app signs users in with an HttpOnly cookie: the browser never hands that cookie to a
 * cross-origin script, so this route, on the app's own origin, receives it and passes it to GeckoGraph
 * when a conversation starts. GeckoGraph then calls the app's API with the signed-in user's own session.
 *
 *   /api/geckograph/api/agent/...  -> the GeckoGraph agent
 *   /api/geckograph/api/...        -> the rest of GeckoGraph (the widget's bootstrap)
 *
 * Config (server-side env, never NEXT_PUBLIC_):
 *   GECKOGRAPH_AGENT_URL       default https://app.geckograph.com
 *   GECKOGRAPH_DISCOVERY_URL   default https://app.geckograph.com
 *   GECKOGRAPH_TARGET_ORIGIN   optional: the origin the agent calls the app's API at, when it is not this one
 *   GECKOGRAPH_DEV_TOKEN       on a developer's machine only: written by `geckograph widget_config`, renewed by
 *                              `geckograph dev`. Never set it in a deploy.
 *
 * GeckoGraph's relearn never turns /api/geckograph/* into a tool, so it needs no geckograph.yml line.
 */

const AGENT_URL = process.env.GECKOGRAPH_AGENT_URL ?? "https://app.geckograph.com";
const DISCOVERY_URL = process.env.GECKOGRAPH_DISCOVERY_URL ?? "https://app.geckograph.com";

/** The request headers the widget sends. Nothing else reaches GeckoGraph; the Cookie header never does. */
const FORWARD_HEADERS = [
  "content-type",
  "accept",
  "origin",
  "referer",
  "x-agentify-api-key",
  "x-geckograph-dev-token",
];

/** Encoding and length describe the upstream bytes, not what this route sends; a cookie must not land on the app. */
const STRIP_RESPONSE_HEADERS = new Set(["content-encoding", "content-length", "transfer-encoding", "connection", "set-cookie"]);

/** The agent calls that carry the dev token in their body: a chat's start and its messages. */
const DEV_TOKEN_BODY = /^\/api\/agent\/conversations\/(start|[^/]+\/messages(\/stream)?)$/;

/** Headers that only a proxy or tunnel in front of the app adds: their presence means the caller is not on this machine. */
const PROXY_HEADERS = [
  "forwarded",
  "via",
  "x-real-ip",
  "x-forwarded-server",
  "true-client-ip",
  "cf-connecting-ip",
  "cf-ray",
];

const LOOPBACK_HOSTS = new Set(["localhost", "127.0.0.1", "[::1]"]);

/** A host or address as a forwarding header writes it (`localhost:3000`, `127.0.0.1`, `::1`, `[::1]:3000`) is loopback. */
function isLoopbackAddress(entry: string): boolean {
  let a = entry.trim().toLowerCase();
  if (a.startsWith("[")) a = a.slice(1, a.indexOf("]") > 0 ? a.indexOf("]") : undefined);
  else if (a.split(":").length === 2) a = a.slice(0, a.indexOf(":"));
  return a === "localhost" || a === "::1" || /^(::ffff:)?127\.\d{1,3}\.\d{1,3}\.\d{1,3}$/.test(a);
}

function isLoopbackOrigin(value: string | null): boolean {
  if (!value) return false;
  try {
    return LOOPBACK_HOSTS.has(new URL(value).hostname);
  } catch {
    return false;
  }
}

/**
 * Whether a proxy or tunnel passed the request on. Next.js itself sets X-Forwarded-Host to the Host header and
 * X-Forwarded-For to the socket's address when they are missing (and a local rewrite proxy sends its own loopback
 * host), so those, and the X-Original- copies a forwarded-headers step leaves, count only when they name a host or
 * address off this machine: a tunnel that rewrites Host to localhost still names its public host or its caller there.
 */
function viaProxy(req: NextRequest): boolean {
  if (PROXY_HEADERS.some((name) => req.headers.has(name))) return true;
  return ["x-forwarded-host", "x-forwarded-for", "x-original-host", "x-original-for"].some((name) => {
    const value = req.headers.get(name);
    return !!value && value.split(",").some((a) => !isLoopbackAddress(a));
  });
}

/**
 * The page's origin as the browser itself sent it: the Origin header, or, on a GET (browsers send no Origin on a
 * same-origin GET), the origin of its Referer. Never an origin this route works out from the Host header, which a
 * tunnel can rewrite to localhost.
 */
function browserOrigin(req: NextRequest): string | null {
  const origin = req.headers.get("origin");
  if (origin) return origin;
  if (req.method !== "GET" && req.method !== "HEAD") return null;
  try {
    const referer = req.headers.get("referer");
    return referer ? new URL(referer).origin : null;
  } catch {
    return null;
  }
}

/**
 * The developer's dev token, for a page on their own machine in development. The relay attaches it to each call, so
 * the token is read fresh from the local config and never sits in a page or a build. Only for a request the browser
 * sent from a loopback page straight to this server: never through a tunnel or proxy, whatever Host says.
 */
function localDevToken(req: NextRequest): string | undefined {
  const token = process.env.GECKOGRAPH_DEV_TOKEN?.trim();
  if (!token || process.env.NODE_ENV === "production") return undefined;
  if (viaProxy(req) || !isLoopbackOrigin(browserOrigin(req))) return undefined;
  return token;
}

function refuse(status: number, message: string): Response {
  return new Response(message, { status });
}

async function relay(req: NextRequest, path: string[]): Promise<Response> {
  const self = req.nextUrl.origin;
  // Same origin removes CORS, not CSRF: a state-changing call must come from this app's own pages.
  if (req.method !== "GET" && req.method !== "HEAD") {
    const origin = req.headers.get("origin");
    const site = req.headers.get("sec-fetch-site");
    if (origin ? origin !== self : site === "cross-site" || site === "cross-origin") {
      return refuse(403, "Cross-origin request rejected");
    }
    // A browser names where a state-changing call comes from; a call that says neither is not from this app's pages.
    if (!origin && !site) return refuse(403, "Request without Origin or Sec-Fetch-Site rejected");
  }
  if (path.length < 2 || path[0] !== "api" || path.some((s) => !s || s === "." || s === ".." || /[\\/]/.test(s))) {
    return refuse(404, "Unknown GeckoGraph route");
  }
  const sub = `/${path.map(encodeURIComponent).join("/")}`;
  const isAgent = path[1] === "agent";
  const base = isAgent ? AGENT_URL : DISCOVERY_URL;
  const url = new URL(sub, base);
  if (url.origin !== new URL(base).origin) return refuse(400, "Bad GeckoGraph route");
  url.search = req.nextUrl.search;

  const headers = new Headers();
  for (const name of FORWARD_HEADERS) {
    const value = req.headers.get(name);
    if (value) headers.set(name, value);
  }
  // A same-origin GET often carries no Origin; GeckoGraph checks the page's origin, so name it.
  if (!headers.get("origin")) headers.set("origin", self);
  const devToken = localDevToken(req);
  if (devToken) headers.set("x-geckograph-dev-token", devToken);
  // The MCP panel's calls carry its widget session as a bearer token. Forward that one kind only: the app's own
  // tokens never reach GeckoGraph.
  const auth = req.headers.get("authorization");
  if (auth?.startsWith("Bearer ggws_")) headers.set("authorization", auth);

  let body: string | undefined;
  if (req.method !== "GET" && req.method !== "HEAD") {
    body = await req.text();
    if (isAgent && sub === "/api/agent/conversations/start") {
      try {
        const start = JSON.parse(body || "{}");
        const cookie = req.headers.get("cookie");
        // Passed as is, so a signed session cookie still verifies byte for byte.
        if (cookie) start.auth = { ...(start.auth ?? {}), cookies: cookie };
        const target = process.env.GECKOGRAPH_TARGET_ORIGIN;
        if (target) start.targetOrigin = new URL(target).origin;
        if (devToken) start.devWidgetToken = devToken;
        body = JSON.stringify(start);
      } catch {
        return refuse(400, "Conversation start is not JSON");
      }
    } else if (isAgent && devToken && DEV_TOKEN_BODY.test(sub)) {
      try {
        const message = JSON.parse(body || "{}");
        if (message && typeof message === "object" && !Array.isArray(message)) {
          message.devWidgetToken = devToken;
          body = JSON.stringify(message);
        }
      } catch {
        // Not JSON: passed on as it came.
      }
    }
  }

  let upstream: Response;
  try {
    upstream = await fetch(url, { method: req.method, headers, body, signal: req.signal, redirect: "manual" });
  } catch (error) {
    if ((error as Error)?.name === "AbortError") return new Response(null, { status: 499 });
    return refuse(502, "GeckoGraph unavailable");
  }
  const out = new Headers();
  upstream.headers.forEach((value, key) => {
    if (!STRIP_RESPONSE_HEADERS.has(key.toLowerCase())) out.set(key, value);
  });
  if (out.get("content-type")?.includes("text/event-stream")) {
    // The reply streams: no proxy in front of the app may buffer it.
    out.set("cache-control", "no-cache, no-transform");
    out.set("x-accel-buffering", "no");
  }
  return new Response(upstream.body, { status: upstream.status, headers: out });
}

type Context = { params: Promise<{ path: string[] }> };

export async function GET(req: NextRequest, ctx: Context) {
  return relay(req, (await ctx.params).path);
}

export async function POST(req: NextRequest, ctx: Context) {
  return relay(req, (await ctx.params).path);
}

export async function DELETE(req: NextRequest, ctx: Context) {
  return relay(req, (await ctx.params).path);
}
