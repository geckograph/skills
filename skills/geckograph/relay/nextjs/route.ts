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
        body = JSON.stringify(start);
      } catch {
        return refuse(400, "Conversation start is not JSON");
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
