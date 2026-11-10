---
name: geckograph-mcp-auth
description: Builds a SaaS app's side of GeckoGraph's MCP session bridge, so the app's end users sign GeckoGraph in through the app's own session instead of a password. Use when adding GeckoGraph MCP sign-in, "Continue with {app}", the session bridge, bridge links, a sessionUrl or revokeUrl endpoint, or onBridgeLinkRequest to an app, or when `geckograph mcp auth check` fails.
---

# GeckoGraph MCP auth: the session bridge

## What you are building

GeckoGraph serves the app's end users through MCP (Claude, ChatGPT, Cursor and other agents). With the session bridge on, GeckoGraph gets a session for one end user by asking the app, instead of signing in with that user's password. Each end user who turns it on gets a **bridge link**: a `linkId` and a random `secret` for that one user. GeckoGraph keeps the secret; the app keeps only its SHA-256.

You add four things to the app, in its own stack. Nothing changes on its login page.

1. **The frontend callback.** The GeckoGraph panel calls `onBridgeLinkRequest(request)`. The app's frontend posts `request` to the app's backend over the user's own app session.
2. **The redeem.** The backend calls GeckoGraph with its `mcp:provision` key and the signed-in user's `externalUserId`, gets `{ linkId, secret }` once, and stores `sha256(secret)` keyed by `linkId`, against the **session's** user.
3. **`POST {sessionUrl}`.** GeckoGraph sends `{ externalUserId, linkId, secret }`; the app answers a **new** session for that user, started by its own sign-in code.
4. **`POST {revokeUrl}`.** GeckoGraph sends `{ event: "link.revoked", externalUserId, linkId }`; the app deletes that link's hash and answers `204`.

Both endpoints check GeckoGraph's signed JWT first. Read these before writing code:

- [contract.md](contract.md): every request and answer, every refusal and its exact answer, and the API claim path.
- [verify-jwt.md](verify-jwt.md): checking the JWT against GeckoGraph's JWKS, step by step.
- [testing.md](testing.md): the tests to write, one per refusal.
- `examples/express.ts` (TypeScript, Express) and `examples/fastapi.py` (Python, FastAPI): the whole backend side in one file each. Use them for the shape; write the code in the app's own framework, data layer and style.

## Steps

1. **Find the app's sign-in code.** Find the function the app already runs after a successful sign-in to start a user's session: it creates the session row or signs the session token, and sets the cookie or returns the token the frontend uses. Search the login handler (`login`, `signIn`, `authenticate`, `createSession`, `issueToken`, `setCookie`, `req.login`, `SignInAsync`), and follow it past the password check to the call that starts the session. If the app signs in through SSO or magic links only, follow that callback the same way. You need a function that starts a session for **one user id**, with no password and no second factor, and returns what the frontend would get. When the code that starts the session is mixed with the password check, split it out into a function both the login and the bridge call. Never call the login route with a stored password, never reuse one of the user's open sessions, and never mint a session of another kind than the frontend gets: GeckoGraph replays it exactly as the app's frontend does.
2. **Add the link table** (contract.md, "The link table"). One row per link, never one per user.
3. **Add the backend redeem route** that the frontend posts to. It takes the user from the app's session, never from the request body.
4. **Pass `onBridgeLinkRequest`** where the app mounts the GeckoGraph panel, on the page it registers as the panel URL. That page must keep GeckoGraph's query parameters, also through the app's login redirect: a signed-out user signs in first and comes back to the same URL.
5. **If the app starts connect links from its backend** (the provisioning API's `connect-sessions`), store the `{ linkId, secret }` the claim answers (contract.md, "The API claim path").
6. **Add `POST {sessionUrl}` and `POST {revokeUrl}`**, each behind the JWT check (verify-jwt.md). Read the raw body bytes before any JSON parsing.
7. **Write the tests** in testing.md, and run them.
8. **Register the three URLs** for one environment in the GeckoGraph dashboard (the app's Session bridge tab): the session URL, the revoke URL and the panel URL, `https`, on the app's verified domain. Each environment (staging, production) has its own deployment, so its own URLs. Turn the bridge on in staging first.
9. **Check it end to end** with `geckograph mcp auth check` (below).

## Rules

- Store a link's hash against the user of the **session** that posted the request, never against an id taken from a request body, a query string or the link request itself.
- Keep one hash **per link**, keyed by `linkId`. A user may hold more than one link (their own, and a check's beside it). A redeem never replaces or deletes another link's hash.
- `sessionUrl` answers `404 {"error":"no_link"}` for an unknown link, a wrong secret, a link of another user, and an inactive user, with the same body and no detail on which.
- Any failed JWT check answers `401` before the body is used.
- Never log the secret, the session you return, the JWT or the request body. Log the `linkId` and the outcome only.
- The secret is never sent to the browser: the frontend posts only the link request.
- Use GeckoGraph's public JWKS. There is no signing secret to configure, and nothing of GeckoGraph's goes in the app's repo.

## Check it: `geckograph mcp auth check`

Once the endpoints are deployed to an environment and registered there, the developer runs:

```bash
geckograph mcp auth check --app <slug> --env <environment>
```

- It needs the CLI signed in (`geckograph login`). `--env` may be left out when exactly one environment has the bridge on; otherwise it lists them.
- It opens the app's panel page in the browser and prints the URL. The developer, signed in to the app as themselves, confirms there. The check makes a test link for that user, asks `sessionUrl` for a session, checks the user's own link still works, makes one read through MCP with that session (a GET tool that reads and needs no input, the first by name), revokes the test link, and checks that `sessionUrl` then answers `no_link`, that a bare `404` and a bad signature are read correctly.
- It prints each step with ✓ or ✗ and only its outcome (endpoint, status, error code, the read's tool), then a final line. Exit 0 means passed. The read shows – when it was not run (no such tool) or was inconclusive (a `404`, a `422`); neither fails the check.
- A person runs it at their desk, signed in to the app. It is not a CI step: run the tests in testing.md in CI.

| Step | ✗ usually means |
| --- | --- |
| `link_redeemed` | The frontend did not post the request, the backend redeem failed, or the `externalUserId` is not the signed-in user's. |
| `session` | `sessionUrl` did not answer `200` with `{ headers, cookies, expiresAt }`: a JWT check that refuses GeckoGraph's token, a hash stored wrongly, or a session the app's API does not accept. |
| `own_link_session` | The check's redeem replaced the user's own link's hash. Keep one hash per `linkId`. |
| `read` | The app refused the session `sessionUrl` returned (a `401`, a `403`, a redirect to sign-in), or did not answer. The session must carry what the app's API needs for that user: its headers and cookies. |
| `revoke` | `revokeUrl` did not answer `204`. |
| `no_link_after_revoke` | The revoke did not delete the hash, or `sessionUrl` answers something other than `404 {"error":"no_link"}` for a gone link. |
| `bare_404` | `sessionUrl` answers a link it does not know with something other than `404 {"error":"no_link"}`. |
| `bad_signature` | `sessionUrl` accepted a token with a bad signature, or refused it with something other than `401`. |
