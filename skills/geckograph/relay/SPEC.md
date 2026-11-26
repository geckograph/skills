# The GeckoGraph relay: what any stack's relay must do

A relay is needed only when the app's API knows the signed-in user from a session cookie the page can't read (HttpOnly),
or from a cookie as well as a token. The browser won't send that cookie to GeckoGraph's origin, so the app's own server
passes it on. An app whose API trusts a token the page holds needs no relay: see the skill's widget section.

The templates beside this file (`aspnetcore`, `aspnet-framework`, `nextjs`) meet this spec. For any other stack, write
a relay in the stack's own idiom that does exactly this, no more.

1. **Mount.** Serve `/api/geckograph/` on the app's own origin. Accept GET and POST, and DELETE where the stack routes
   it. The rest of the path must be `api/<segment>/…`, at least two segments. Refuse an empty segment, `.`, `..` or a
   backslash with 404.
2. **Upstreams.** `api/agent/…` goes to the Agent Service, every other `api/…` to Discovery. Read both addresses from
   settings, defaulting to `https://app.geckograph.com`. Append the original path and query to the base, and refuse a
   result whose origin is not the base's (400). Never follow redirects.
3. **Same origin only.** On every request but GET: when `Origin` is present it must equal the request's own
   `scheme://host`; when it is absent, refuse `Sec-Fetch-Site: cross-site` or `cross-origin`, and refuse a request that
   has neither `Origin` nor `Sec-Fetch-Site` (a browser always sends one of them; anything else is not the app's own
   page). Refuse with 403. Behind a proxy that terminates TLS, honour the forwarded headers first, or every POST fails
   this check.
4. **Request headers.** Forward only `Content-Type`, `Accept`, `Origin`, `Referer`, `X-Agentify-Api-Key` and
   `X-GeckoGraph-Dev-Token`. Set `Origin` to the relay's own origin when the request has none. Forward `Authorization`
   only when it starts with `Bearer ggws_` (the MCP panel's session). Never forward the `Cookie` header itself.
5. **The cookie, only when a chat starts.** On `POST api/agent/conversations/start` only, put the incoming `Cookie`
   header string, unchanged, into the JSON body as `auth.cookies`, merged into any `auth` object already there. A start
   body that isn't JSON gets 400.
6. **Response.** Copy every response header except `Content-Encoding`, `Content-Length`, `Transfer-Encoding`,
   `Connection` and `Set-Cookie`. Stream the body as it arrives. For `text/event-stream`, also set
   `Cache-Control: no-cache, no-transform` and `X-Accel-Buffering: no`.
7. **Errors.** An unreachable upstream is 502. Pass every upstream status through unchanged.
8. **Dev token, on a development machine only.** Only under the stack's own development switch, and only for a request
   a browser on this machine sent straight to this server:
   - The page's origin is the one the browser sent: its `Origin` header, or on a GET with no `Origin` (browsers send
     none on a same-origin GET) its `Referer`'s origin. It must be loopback (`localhost`, `127.0.0.1`, `[::1]`). Never
     use the `Origin` the relay filled in from its own address (step 4), or anything else worked out from the `Host`
     header: a tunnel can rewrite `Host` to `localhost` (`ngrok --host-header=localhost:<port>`), and a stranger's
     request would then look local.
   - Where the stack exposes the connection's own address, it is loopback, so another machine on the network never
     gets the token (the ASP.NET templates check it; a Next.js route handler can't see it, so run `next dev -H
     127.0.0.1` on a shared network).
   - No proxy or tunnel passed it on: no `Forwarded`, `Via`, `X-Real-IP`, `X-Forwarded-Server`, `True-Client-IP`,
     `CF-Connecting-IP` or `CF-Ray` header, and no `X-Forwarded-Host`, `X-Original-Host`, `X-Forwarded-For` or
     `X-Original-For` naming a host or address that is not loopback. (A local front server, such as IIS hosting
     ASP.NET Core out of process or a local rewrite proxy, names this machine in those; Next.js sets them on every
     request itself. A tunnel names its public host or its caller.)
   - What stays open: a tunnel or port forward that adds no forwarding header (`ssh -R`, `ngrok tcp`, a raw TCP
     forwarder) and a caller who forges a loopback `Origin` look exactly like a local browser. Never expose a dev server
     that holds a dev token through one.

   Then attach the developer's current dev token as the
   `X-GeckoGraph-Dev-Token` header, and as `devWidgetToken` in the JSON body of `conversations/start`,
   `conversations/{id}/messages` and `conversations/{id}/messages/stream` (a message body that isn't JSON passes
   through unchanged). Read it fresh for each request from a source `geckograph dev` keeps current: a relay written from
   this spec asks `geckograph dev`'s local endpoint, `GET http://127.0.0.1:47613/widget/dev-token?key=<env key>` with
   the header `Origin: http://localhost`, and uses the `token` field of its JSON answer. The templates read the local
   config file `geckograph widget_config` wrote, which `geckograph dev` rewrites. Never on a deployed server.
9. **Optional.** `targetOrigin` on the start, from a setting, only for a local Docker setup where the app's API answers
   on another host than the page.
