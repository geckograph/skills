# Tests to write

Write these in the app's own test framework, against the real routes (HTTP-level tests through the app's router), with the app's test database. Each refusal has its own test, and each test fails when its check is removed: run it once with the check commented out and see it fail.

## A test signer

GeckoGraph's signing key is not available to tests, so tests sign with a key of their own:

- Generate an ES256 (P-256) key pair in the test setup.
- Point the app's JWKS URL at a local JWKS that holds the test public key (a stub server, or the HTTP mocking the app's tests already use), with a `kid`.
- A helper `sign({ aud, body, ...overrides })` builds the header `{ alg: "ES256", typ: "geckograph-bridge+jwt", kid }` and the claims `iss`, `aud`, `iat = now`, `exp = now + 60`, a fresh random `jti` and `body_sha256 = sha256hex(body bytes)`, and lets a test override any of them.
- Send the body as the exact bytes that were hashed.

Never commit a real key, and never point tests at GeckoGraph.

## `sessionUrl`

| Test | Expect |
| --- | --- |
| a good token, a stored link of an active user, the right secret | `200`, `headers`/`cookies`/`expiresAt`, and the session works on an authenticated route of the app's API |
| the session is new: two calls give two different sessions, and neither is one the user had open | different values |
| no `Authorization` header | `401` |
| a token signed by another key (same `kid`) | `401` |
| `alg` `HS256` signed with the public key as the secret, and `alg` `none` | `401` |
| `typ` missing or another value | `401` |
| `iss` another value | `401` |
| `aud` the revoke URL | `401` |
| `aud` the session URL with a trailing slash, or another host | `401` |
| `exp` in the past (beyond the skew) | `401` |
| `exp - iat` = 61 seconds | `401` |
| `iat` in the future (beyond the skew) | `401` |
| `body_sha256` of another body (the body changed after signing) | `401` |
| the same body re-serialised (spacing, key order) under the original token | `401` |
| the same token sent twice | the first `200`, the second `401` |
| an unknown `linkId` | `404`, body exactly `{"error":"no_link"}` |
| a wrong secret | `404 {"error":"no_link"}` |
| another user's `linkId` with that user's right secret, under this `externalUserId` | `404 {"error":"no_link"}` |
| an unknown `externalUserId` | `404 {"error":"no_link"}` |
| an inactive (disabled, deleted, locked) user | `404 {"error":"no_link"}` |
| the four `404` cases | identical bodies |
| a good token over a body that is not the JSON object | `400` |

## `revokeUrl`

| Test | Expect |
| --- | --- |
| a good token for a stored link | `204`, the row is gone, and `sessionUrl` then answers `404 {"error":"no_link"}` for it |
| the user holds two links; one is revoked | the other still gets `200` from `sessionUrl` |
| a link that is already gone | `204` |
| `aud` the session URL | `401`, the row stays |
| a bad signature, an expired token, a changed body, a replayed `jti` | `401` each, the row stays |
| `event` another value | `400`, the row stays |

## The widget session route

Mock GeckoGraph's widget sessions endpoint.

| Test | Expect |
| --- | --- |
| a signed-in user asks for one | GeckoGraph is called with the app's provision key, **the session's user's** `externalUserId` and the bridge environment; the page gets `widgetSession` and `expiresAt` |
| the body also carries another user's id | it is ignored: the call names the session's user |
| not signed in | the app's usual `401` or login redirect; GeckoGraph is not called |
| GeckoGraph answers not `201` | an error to the page, with no detail |

## The redeem route (the frontend's post)

Mock GeckoGraph's redeem endpoint.

| Test | Expect |
| --- | --- |
| a signed-in user posts a request | the redeem is called with the app's provision key and **the session's user's** `externalUserId`; one row is stored for that user with `sha256(secret)`, never the secret |
| the body also carries another user's id (`externalUserId`, `userId`) | it is ignored: the redeem names the session's user, the row is the session's user's |
| not signed in | the app's usual `401` or login redirect; the redeem is not called |
| GeckoGraph answers not `200` | an error to the frontend, no row |
| the user already holds a link, and redeems a second one | two rows; the first link still gets `200` from `sessionUrl` |
| the answer to the frontend | holds no secret and no `linkId`-secret pair |

## End to end

The tests above run in CI. Once deployed to an environment and registered, a developer runs `geckograph mcp auth check --app <slug> --env <environment>` at their desk, signed in to the app (SKILL.md), or a person runs "Test MCP auth" on the dashboard's Session bridge tab from their own signed-in browser.
