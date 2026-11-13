# The session bridge contract

`{GeckoGraph}` below is `https://app.geckograph.com`. `{app}` is the app's slug in GeckoGraph. `externalUserId` is the app's own id for a user: the same opaque string the app already sends GeckoGraph (widget sessions, connect sessions), unique in the app.

## 0. What the app needs from GeckoGraph

A person sets these up in the GeckoGraph dashboard, on the **MCP** page. Only an owner or admin of the GeckoGraph organization can.

- **A verified domain** (Domain tab). The person starts verification, adds the TXT record it shows at the domain's DNS provider, and clicks "Check now". One check covers all subdomains. Provisioning keys, widget sessions and the bridge switch all need it.
- **An `mcp:provision` key** (Provisioning tab, "New provisioning key"). It is scoped to this one app and can be created only once the domain is verified. It is shown once. It is a server secret: put it in each environment's deploy config (app setting, secret store or pipeline variable), never in the repo and never in the browser. Every call to `/api/mcp/v1/apps/{app}/…` sends it as `Authorization: Bearer agfy_…`, from the app's server: a request with an `Origin` header is refused.
- **The bridge environment.** The environment (staging, production) the bridge is registered and turned on in (Session bridge tab). Its **Headless MCP** switch is on, and it lists the app's page origins (both on the Environments page).

## 1. Making a link

A link is made only by the app's backend, for the user signed in to the app. GeckoGraph can't make one alone.

### 1a. The backend creates a widget session

The GeckoGraph panel runs on the app's page, for the signed-in user. It acts with a **widget session** that the app's backend creates for that user:

```http
POST {GeckoGraph}/api/mcp/v1/apps/{app}/widget-sessions
Authorization: Bearer agfy_…            (the app's mcp:provision key)
Content-Type: application/json

{ "externalUserId": "<the session's user's id>", "environment": "<the bridge environment>" }
```

```http
201 Created
{ "widgetSession": "ggws_…", "expiresAt": "2026-11-12T15:04:05.000Z" }
```

- Add a backend route the page calls over the user's own app session (for example `POST /api/geckograph/widget-session`). It takes the user from the **session**, never from the body, and answers `{ widgetSession, expiresAt }`.
- `environment` is the environment's name. Name the bridge environment: the panel serves the bridge of its widget session's environment only. Leave it out and the app's default environment is used.
- A widget session lasts 1 hour. Before `expiresAt`, call the route again and pass the new value to the panel's `setWidgetSession` (1b). The panel reloads with it.
- It works only from the origins the environment lists when it is created. After adding an origin, create a new one.
- Never log it. It goes to the browser, for this one user only.

| Refusal | Answer | Fix |
| --- | --- | --- |
| the app's domain is not verified | `403 {"error":"domain_unverified"}` | Verify the domain (section 0). |
| Headless MCP is off in that environment | `403 {"error":"headless_off"}` | Turn on Headless MCP for the environment. |
| the environment lists no origins | `409 {"error":"no_origins", "message": "…"}` | Add the app's origin to the environment on the Environments page. |
| no such environment | `404 {"error":"not_found"}` | Name an environment of the app. |
| a body field is missing or not allowed | `400 {"error":"invalid_request", "field": "…"}` | Send only `externalUserId` and `environment`. |
| no key, or not a live key | `401 {"error":"invalid_key"}` | Send the `mcp:provision` key. |
| not this app's `mcp:provision` key, or an `Origin` header | `403 {"error":"forbidden"}` | Use the key made for this app, from the server. |
| more than 30 a minute for one key (shared with connect sessions) | `429 {"error":"rate_limited"}` | Create one per page load, not per render. |

### 1b. The page loads the panel

The panel is in GeckoGraph's embed script, the same script as the chat widget. GeckoGraph serves it; there is no package to install:

```html
<script src="https://app.geckograph.com/geckograph-embed.min.js"></script>
```

The script sets `window.GeckoGraph` (also reachable as `window.AgentifyEmbed`). With a `data-app` attribute, the tag also starts the chat widget. Without one, it starts no chat (it logs a "Missing required data-app attribute" warning in the console) and only provides `GeckoGraph.mountMcpPanel`. When the page already has the chat widget's tag, use that one: don't load the script twice.

Mount the panel once the script has loaded:

```ts
async function newWidgetSession(): Promise<{ widgetSession: string; expiresAt: string }> {
  const res = await fetch('/api/geckograph/widget-session', { method: 'POST', credentials: 'include' });
  if (!res.ok) throw new Error('no widget session');
  return res.json();
}

const first = await newWidgetSession();
const panel = GeckoGraph.mountMcpPanel('#geckograph-mcp', {
  discoveryUrl: 'https://app.geckograph.com',
  widgetSession: first.widgetSession,
  onBridgeLinkRequest: async (request) => {
    // `request` is an opaque string. Post it to the app's own backend over the user's own session.
    const res = await fetch('/api/geckograph/bridge-links', {
      method: 'POST',
      credentials: 'include',         // the app's session cookie, or the app's usual auth header
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ request }),
    });
    if (!res.ok) throw new Error('bridge link refused');   // the panel says "{app} didn't accept the connection"
  },
});

// A widget session lasts an hour: refresh it a few minutes before it ends.
let expiresAt = Date.parse(first.expiresAt);
setInterval(async () => {
  if (expiresAt - Date.now() > 5 * 60 * 1000) return;
  const next = await newWidgetSession();
  expiresAt = Date.parse(next.expiresAt);
  panel.setWidgetSession(next.widgetSession);
}, 60 * 1000);
```

- `mountMcpPanel(element, options)` takes an element or a CSS selector, and returns `{ setWidgetSession, destroy }`. `options.theme` is `'light'`, `'dark'` or `'auto'` (the default).
- When a signed-in user turns the bridge on in the panel (or at "Continue with {app}", or when a developer confirms an auth check), the panel calls `onBridgeLinkRequest(request)`.
- The callback resolves when the backend stored the link, and throws (rejects) when it did not.
- The link request lasts 5 minutes and works once.
- The page the panel is mounted on is the **panel URL** you register. GeckoGraph opens it with its own query parameters (`gg_authorize` for "Continue with {app}", `gg_check` for an auth check). Keep them, also through the app's login redirect, so a signed-out user signs in and comes back to the same URL.
- Use the app's usual CSRF protection on both backend routes, as on any state-changing route.

### 1c. The backend redeems it

The backend route takes the user from the **session**, never from the body, and redeems:

```http
POST {GeckoGraph}/api/mcp/v1/apps/{app}/bridge-links/redeem
Authorization: Bearer agfy_…            (the app's mcp:provision key, from the app's server config)
Content-Type: application/json

{ "request": "<the link request the frontend posted>", "externalUserId": "<the session's user's id>" }
```

```http
200 OK
{ "linkId": "0b6f6f0e-3c1a-4c55-9a43-2f7d8e1b9c10", "secret": "3vJ9…" }
```

- The answer is given once. Store it at once:

  ```sql
  INSERT INTO geckograph_bridge_links (link_id, user_id, secret_sha256) VALUES ($1, $2, $3)
  ```

  with `user_id` the session's user and `secret_sha256` the lowercase hex SHA-256 of the secret string's UTF-8 bytes. Never store the secret itself, and never return it to the browser.
- A new link is a new row. Never update or delete another link's row here, even one of the same user.
- GeckoGraph refuses the redeem unless the `externalUserId` is the user the link request was made for, the key is the app's own, and the request is live and unused. On any answer that is not `200`, store nothing, and answer the frontend with an error (any non-2xx, with no detail) so the panel says the connection was not accepted.
- The call is server to server: no browser calls it, and a request with an `Origin` header is refused.

## 2. The link table

```sql
CREATE TABLE geckograph_bridge_links (
  link_id        text PRIMARY KEY,                 -- GeckoGraph's linkId, a UUID
  user_id        <the app's user id type> NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  secret_sha256  char(64) NOT NULL,                -- lowercase hex
  created_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX geckograph_bridge_links_user ON geckograph_bridge_links (user_id);
```

- No unique constraint on `user_id`: a user may hold several links (their own, a check's).
- Deleting a user deletes their links. Deactivating a user makes `sessionUrl` refuse them (below).
- Use the app's own migration tool and naming.

## 3. The two endpoints GeckoGraph calls

Both are `POST`, `application/json`, at URLs you register for one environment. Each registered URL (the session URL, the revoke URL and the panel URL) is `https`, has no query string, fragment or user info, and is on the app's registrable domain: the domain of the app's address in GeckoGraph (for `https://app.example.com`, any host under `example.com`). The dashboard refuses any other URL. Every call carries `Authorization: Bearer <JWT>`. Check it first ([verify-jwt.md](verify-jwt.md)); any failure answers `401` and nothing else happens.

### `POST {sessionUrl}`

```http
POST https://app.example.com/api/geckograph/bridge/session
Authorization: Bearer eyJhbGciOiJFUzI1NiIsInR5cCI6Imdl…
Content-Type: application/json

{ "externalUserId": "user_123", "linkId": "0b6f6f0e-3c1a-4c55-9a43-2f7d8e1b9c10", "secret": "3vJ9…" }
```

Find the link row by `linkId`. Answer `200` only when all of these hold:
- the row exists;
- its user is the user `externalUserId` names;
- `sha256(secret)` equals `secret_sha256` (compare in constant time);
- the user is active (not disabled, deleted, locked or otherwise unable to sign in to the app now).

Then start a **new** session for that user with the app's own sign-in code, the same function the login runs after the password check, and answer what the app's frontend gets:

```http
200 OK
Content-Type: application/json

{
  "headers": { "Authorization": "Bearer eyJ…" },
  "cookies": [
    { "name": "session", "value": "s%3A…" }
  ],
  "expiresAt": "2026-11-10T18:30:00Z"
}
```

- `headers`: the request headers the frontend sends to the app's API with this session (name to value). `{}` when there are none. An `Authorization` header is `Scheme value`, such as `Bearer eyJ…`, with a space between the two. GeckoGraph drops an `Authorization` value without a scheme (a bare token).
- `cookies`: the cookies the sign-in sets, each with `name` and `value`. `[]` when there are none. One of `headers` and `cookies` is not empty.
- `expiresAt`: when the session ends, ISO 8601 in UTC. GeckoGraph asks again after that, or when a call comes back signed out.
- GeckoGraph sends every header and one `Cookie` header holding every cookie (`name=value; name=value`) on each call it makes to the app's API with this session. It reads only each cookie's `name` and `value`: a `domain`, `path` or other attribute is ignored, and no cookie is held back by host. So return only the headers and cookies the app's API needs for this user, never cookies of other services.
- Never return one of the user's open sessions, and never a longer-lived or wider session than the frontend gets.

In every other case answer exactly this, with the same body whichever check failed:

```http
404 Not Found
Content-Type: application/json

{"error":"no_link"}
```

| Case | Answer |
| --- | --- |
| a JWT check fails (signature, `typ`, `alg`, `iss`, `aud`, `exp`, `iat`, body hash, replayed `jti`, missing header) | `401` |
| no row for `linkId` | `404 {"error":"no_link"}` |
| the row's user is not `externalUserId`'s user, or no such user | `404 {"error":"no_link"}` |
| the secret's hash doesn't match | `404 {"error":"no_link"}` |
| the user is inactive | `404 {"error":"no_link"}` |
| the body is not the JSON above (after a good JWT) | `400` |
| all good | `200 { headers, cookies, expiresAt }` |

How GeckoGraph reads the answer, so the answers above are the only ones to give:
- `200`: a session; GeckoGraph stores it and makes the user's call.
- `404 {"error":"no_link"}`: the link is dead. GeckoGraph deletes its side of it; the user's agent tokens read `needs_sign_in` until they connect again.
- any other `404`, a `401`, a `403`, a `5xx` or a timeout (10 seconds): the endpoint is at fault. The call fails as retryable, nobody is disconnected, and the org's owners and admins are told. After 20 faults in a row GeckoGraph stops calling that environment's `sessionUrl` for 5 minutes. So a framework's default `404` page for a route that is not deployed never ends anyone's link.

### `POST {revokeUrl}`

```http
POST https://app.example.com/api/geckograph/bridge/revoke
Authorization: Bearer eyJhbGciOiJFUzI1NiIsInR5cCI6Imdl…
Content-Type: application/json

{ "event": "link.revoked", "externalUserId": "user_123", "linkId": "0b6f6f0e-3c1a-4c55-9a43-2f7d8e1b9c10" }
```

```sql
DELETE FROM geckograph_bridge_links WHERE link_id = $1 AND user_id = $2
```

```http
204 No Content
```

| Case | Answer |
| --- | --- |
| a JWT check fails | `401` |
| `event` is not `link.revoked`, or the body is not the JSON above | `400` |
| the link was deleted, or was already gone | `204` |

- Delete only the named link. A user's other links stay.
- GeckoGraph calls it when the user disconnects, when a new link of theirs replaces the old one, and when an auth check ends. It deletes its side of the link whether or not this call succeeds.
- The app may end a link on its own by deleting the row; the next `sessionUrl` call answers `no_link`.
- Don't end the user's open app sessions here: a revoke ends GeckoGraph's access, not the user's.

## 4. The signed JWT, in short

Header `{ "alg": "ES256", "typ": "geckograph-bridge+jwt", "kid": "…" }`; claims:

| Claim | Holds |
| --- | --- |
| `iss` | `https://app.geckograph.com` |
| `aud` | the exact endpoint URL you registered (the session URL or the revoke URL) |
| `iat`, `exp` | issued and expiry, seconds; `exp` is at most 60 seconds after `iat` |
| `jti` | a unique id; each is accepted once |
| `body_sha256` | lowercase hex SHA-256 of the exact request body bytes |

Public keys: `https://app.geckograph.com/oauth/jwks.json`. They rotate with an overlap; fetch by `kid`. Full steps in [verify-jwt.md](verify-jwt.md).
