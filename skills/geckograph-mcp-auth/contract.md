# The session bridge contract

`{GeckoGraph}` below is `https://app.geckograph.com`. `{app}` is the app's slug in GeckoGraph. `externalUserId` is the app's own id for a user: the same opaque string the app already sends GeckoGraph (widget sessions, connect sessions), unique in the app.

## 1. Making a link

A link is made only by the app's backend, for the user signed in to the app. GeckoGraph can't make one alone.

### 1a. The panel hands the frontend a link request

When a signed-in user turns the bridge on in the GeckoGraph panel (or at "Continue with {app}", or when a developer confirms an auth check), the panel calls the `onBridgeLinkRequest` callback the app passed when it mounted the panel:

```ts
GeckoGraphEmbed.mountMcpPanel('#geckograph-mcp', {
  discoveryUrl: 'https://app.geckograph.com',
  widgetSession,                      // from the backend's POST /widget-sessions, for this signed-in user
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
```

- The callback resolves when the backend stored the link, and throws (rejects) when it did not.
- The link request lasts 5 minutes and works once.
- The page the panel is mounted on is the **panel URL** you register. GeckoGraph opens it with its own query parameters (an authorization request or a check). Keep them, also through the app's login redirect, so a signed-out user signs in and comes back to the same URL.
- Use the app's usual CSRF protection on this route, as on any state-changing route.

### 1b. The backend redeems it

The backend route takes the user from the **session**, never from the body, and redeems:

```http
POST {GeckoGraph}/api/mcp/v1/apps/{app}/bridge-links/redeem
Authorization: Bearer agfy_…            (the app's mcp:provision key, from the app's server config)
Content-Type: application/json

{ "request": "<the link request the frontend posted>", "externalUserId": "<the session's user's id>" }
```

```http
200 OK
{ "linkId": "bl_7Hq…", "secret": "3vJ9…" }
```

- The answer is given once. Store it at once:

  ```sql
  INSERT INTO geckograph_bridge_links (link_id, user_id, secret_sha256) VALUES ($1, $2, $3)
  ```

  with `user_id` the session's user and `secret_sha256` the lowercase hex SHA-256 of the secret string's UTF-8 bytes. Never store the secret itself, and never return it to the browser.
- A new link is a new row. Never update or delete another link's row here, even one of the same user.
- GeckoGraph refuses the redeem unless the `externalUserId` is the user the link request was made for, the key is the app's own, and the request is live and unused. On any answer that is not `200`, store nothing, and answer the frontend with an error (any non-2xx, with no detail) so the panel says the connection was not accepted.
- The call is server to server: no browser calls it, and a request with an `Origin` header is refused.

### 1c. The API claim path

When the app starts a connect link from its backend (`POST /api/mcp/v1/apps/{app}/connect-sessions`, with its `state`), on a bridge app the claim answers the link too:

```http
POST {GeckoGraph}/api/mcp/v1/apps/{app}/connect-sessions/{id}/claim
{ "code": "<code from the redirect>" }
```

```http
200 OK
{ "id": "…", "token": "…", "name": "…", "scopes": ["read"], "expiresAt": "…", "linkId": "bl_…", "secret": "…" }
```

When the browser comes back to the app's `returnUrl` with `code` and `state`:
1. Check `state` against the returning browser's own session, as for any connect link.
2. Check that the session's user is the user the app started that connect session for (its `externalUserId`). If not, do not claim, or, when the claim already answered, store nothing of it and answer an error.
3. Claim, and store `linkId` and `sha256(secret)` against the session's user, as in 1b.

An answer without `linkId` (the bridge is not on in that environment) stores no link.

## 2. The link table

```sql
CREATE TABLE geckograph_bridge_links (
  link_id        text PRIMARY KEY,                 -- GeckoGraph's linkId
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

Both are `POST`, `application/json`, at `https` URLs you register for one environment, on the app's verified domain. Every call carries `Authorization: Bearer <JWT>`. Check it first ([verify-jwt.md](verify-jwt.md)); any failure answers `401` and nothing else happens.

### `POST {sessionUrl}`

```http
POST https://app.example.com/api/geckograph/bridge/session
Authorization: Bearer eyJhbGciOiJFUzI1NiIsInR5cCI6Imdl…
Content-Type: application/json

{ "externalUserId": "user_123", "linkId": "bl_7Hq…", "secret": "3vJ9…" }
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
    { "name": "session", "value": "s%3A…", "domain": "app.example.com", "path": "/" }
  ],
  "expiresAt": "2026-11-10T18:30:00Z"
}
```

- `headers`: the request headers the frontend sends to the app's API with this session (name to value). `{}` when there are none.
- `cookies`: the cookies the sign-in sets, each with `name`, `value`, `domain` and `path`. `[]` when there are none. One of the two is not empty.
- `expiresAt`: when the session ends, ISO 8601 in UTC. GeckoGraph asks again after that, or when a call comes back signed out.
- Give only headers and cookies for the app's own API hosts. GeckoGraph drops any for a host outside the app's scope.
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

{ "event": "link.revoked", "externalUserId": "user_123", "linkId": "bl_7Hq…" }
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
