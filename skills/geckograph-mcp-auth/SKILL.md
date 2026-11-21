---
name: geckograph-mcp-auth
description: Builds a SaaS app's side of GeckoGraph's MCP session bridge, so the app's end users sign GeckoGraph in through the app's own session instead of a password. Use when adding GeckoGraph MCP sign-in, the GeckoGraph MCP panel or a widget session, "Continue with {app}", the session bridge, bridge links, a sessionUrl or revokeUrl endpoint, or onBridgeLinkRequest to an app, or when `geckograph mcp auth check` fails.
---

# GeckoGraph MCP auth: the session bridge

## What you are building

GeckoGraph serves the app's end users through MCP (Claude, ChatGPT, Cursor and other agents). With the session bridge on, GeckoGraph gets a session for one end user by asking the app, instead of signing in with that user's password. Each end user who turns it on gets a **bridge link**: a `linkId` and a random `secret` for that one user. GeckoGraph keeps the secret; the app keeps only its SHA-256.

**Which user.** Many apps have more than one kind of user, each with its own login, its own user records and its own sessions: customers and staff, members and admins, a consumer app and a back office, a tenant's users and the platform's operators. The bridge is for exactly one of them: the user who signs in to the part of the app GeckoGraph's graph was built on. GeckoGraph's tools call that part's API, with the session that part's login gives, so the session the bridge answers must be that login's session, for a user of that kind. Every piece below belongs to that one user kind: the `externalUserId`, the user the link store points at, the panel page, and the sessions. Step 1 finds it; build nothing before it.

**Which tenant.** In many apps one user can work in several tenants (workspaces, accounts, organizations, projects), and a session belongs to the user **in one tenant**: the token or session row names it, and the app's API acts in that tenant. Then the bridge is per user **and** tenant, never per user: the `externalUserId` names both (for example `<user id>:<tenant id>`), each link is for one user in one tenant, and every session the bridge answers starts in the link's tenant. Never use the user's current, last-used or default tenant: it changes when the user switches, and a token made for one tenant would then read and write another, with no error. The widget session takes the tenant from the page's own session, so the panel shows the tokens of the tenant the user is in. When sessions are not tied to a tenant (the tenant is chosen per request, or the app has none), the bridge is per user.

**No workspaces: public MCP, no bridge code.** An app with no workspaces (one per user, or none) doesn't need the bridge for MCP: the widget gives its end users MCP with no bridge code, and they sign in with their app password on GeckoGraph's page. The widget itself is installed by the `geckograph` skill. A person turns on public MCP on the dashboard's Environments page, which asks them to confirm the app has no workspaces. Its sign-in limits are low on purpose and shown on the MCP overview: anyone can trip one account's cool-down, or pause new public connections for an hour with failed sign-ins, so tell the person. **If the app chose public MCP, stop here: build nothing below.** If your app has workspaces or tenants, add the bridge, and everything below is for the bridge.

You add five things to the app, in its own stack, for the bridge. Nothing changes on its login page.

1. **The widget session route.** The backend calls GeckoGraph with its `mcp:provision` key and the signed-in user's `externalUserId`, and hands the page the `widgetSession` it answers.
2. **The panel and its callback.** The panel page loads GeckoGraph's embed script and mounts the panel with that widget session. The panel calls `onBridgeLinkRequest(request)`; the app's frontend posts `request` to the app's backend over the user's own app session.
3. **The redeem.** The backend calls GeckoGraph with its `mcp:provision` key and the signed-in user's `externalUserId`, gets `{ linkId, secret }` once, and stores `sha256(secret)` keyed by `linkId`, against the **session's** user. (With derived secrets, below, the app makes the `linkId` and `secret` itself and stores nothing.)
4. **`POST {sessionUrl}`.** GeckoGraph sends `{ externalUserId, linkId, secret }`; the app answers a **new** session for that user, started by its own sign-in code.
5. **`POST {revokeUrl}`.** GeckoGraph sends `{ event: "link.revoked", externalUserId, linkId }`; the app deletes that link's hash and answers `204`.

**How the app keeps its links.** By default the app keeps one row per link in a new table (option A). An app that can't add a table derives each link's secret from a key only it holds, an HMAC of the `linkId` and the user, and keeps no record (option B, contract.md, "Option B: derived secrets"). The table is safer: with it the app can end one user's link on its own, and in B its only way to end links itself is to change its key, which ends every user's link. Build A unless the person you work for says the app can't add a table; then build B and tell them that trade-off.

Both endpoints check GeckoGraph's signed JWT first. Read these before writing code:

- [contract.md](contract.md): what the app needs from GeckoGraph first, every request and answer, and every refusal and its exact answer.
- [verify-jwt.md](verify-jwt.md): checking the JWT against GeckoGraph's JWKS, step by step.
- [testing.md](testing.md): the tests to write, one per refusal.
- `examples/express.ts` (TypeScript, Express) and `examples/fastapi.py` (Python, FastAPI): the whole backend side in one file each. Use them for the shape; write the code in the app's own framework, data layer and style. `examples/express-derived.ts`: the routes that differ with derived secrets (option B).

The geckograph skill's rule "Never call the GeckoGraph API directly" is about your own calls while you work. The app's backend code you write here calls GeckoGraph's provisioning API (`/api/mcp/v1/apps/{app}/…`) with the app's `mcp:provision` key: that is the app's own server talking to GeckoGraph, and it is what this skill builds. You never call it yourself, with `curl` or anything else.

## Before you start

A person does these in the GeckoGraph dashboard, on the MCP page, as an owner or admin (contract.md, "What the app needs from GeckoGraph"). Ask them; you can't do them. They are for the deployed environments (staging, production): a local test on the developer's own machine needs none of them ("Try it locally", below).

- Verify the app's domain (Domain tab): a TXT record at the DNS provider, then "Check now".
- Create an `mcp:provision` key (Provisioning tab). It needs the verified domain, and it is shown once. Each environment's deploy needs it set as a server secret, under the setting name `geckograph mcp local` writes locally (`GECKOGRAPH_PROVISION_KEY` on Node, `GeckoGraph:ProvisionKey` on ASP.NET Core, `GeckoGraphProvisionKey` on .NET Framework), beside the bridge environment's name (`GECKOGRAPH_ENVIRONMENT`, `GeckoGraph:Environment`, `GeckoGraphEnvironment`). Never commit it.
- In the environment the bridge is for, turn on Headless MCP and list the app's page origins (Environments page).

## Steps

1. **Find the user the graph signs in as.** Start from the graph, not from the app's code:
   - `geckograph list_features --all` lists the graph's features, `geckograph show_feature --feature <name> --env <environment>` names a feature's tools, and `geckograph show_tool --tool <name> --env <environment>` prints a tool's method and full URL and the inputs it sends (an `authorization` header, a cookie). Read tools from every module, and group the tool URLs by the backend route group they fall under (the controller, router or path prefix).
   - For every one of those route groups, find how it checks who is calling (the middleware, attribute, filter or session lookup in front of it). That check names the user kind: the session or token it accepts, and the user records it loads. All the groups must accept the same user kind; one that accepts another is a second login to raise with the person (below).
   - Find the frontend that serves those pages and calls those routes, and its login: the page, the backend route it posts to, and the code that checks the password, or the SSO or magic-link callback when the login has no password.
   - List every login the app has. When there is more than one, write down which one the graph's routes accept and why (the route, the check, the user records), and tell the person before you build. When the routes accept more than one, or the evidence doesn't single one out, ask the person which user the bridge is for; don't guess.
   - Without the CLI signed in, ask the person for a few of the graph's tool URLs, or which part of the app the graph covers.
   - Find whether that login's session is tied to one tenant: read the token's claims or the session row's columns, and how the API reads the tenant (from the session, or from each request). Tell the person what you found before you build ("Which tenant", above). Then record it now, as part of setup: `geckograph mcp tenancy set --app <slug> --value <none|per_request|per_session> --evidence "<what you read>"` (`none`: one tenant per user, or none; `per_request`: the tenant is in each API call; `per_session`: the session is tied to one tenant). It records what you just told the person, and an owner or admin can correct it by running the same command again, so it needs no separate yes. GeckoGraph shows it to the app's owners, and refuses new password MCP connections to a `per_session` app: that app needs this bridge.
   Build everything below for that user kind only. A user record of another kind, or a session from another login, is wrong even when it looks similar: GeckoGraph's tools would be refused with it.
2. **Find that login's sign-in code.** Find the function that login already runs after a successful sign-in to start a user's session: it creates the session row or signs the session token, and sets the cookie or returns the token the frontend uses. Search the login handler (`login`, `signIn`, `authenticate`, `createSession`, `issueToken`, `setCookie`, `req.login`, `SignInAsync`), and follow it past the password check to the call that starts the session. If the app signs in through SSO or magic links only, follow that callback the same way. You need a function that starts a session for **one user id** (in **one tenant**, when sessions are tied to one: pass the tenant in, never let it read the user's current one), with no password and no second factor, and returns what the frontend would get. When the code that starts the session is mixed with the password check, split it out into a function both the login and the bridge call. Never call the login route with a stored password, never reuse one of the user's open sessions, and never mint a session of another kind than the frontend gets: GeckoGraph replays it exactly as the app's frontend does. If the app's sign-in keeps a record per session (a session, token or device row), each bridge session is a new one of those records, made by that same code, so the app's API accepts it unchanged and the app's own way of ending sessions ends it. When those records have no expiry, give the bridge's records one, and make the app's API refuse a bridge session after it (checked from its creation time, for example). A stored time nobody checks leaves the session live forever. Add a separate store for bridge sessions only when the app's sign-in keeps no record at all, and tell the person why.
3. **Add the link store** (contract.md, "The link store"). It is the app's own table: read its existing schema first and design it like the app's other tables (naming, keys, date types, foreign keys, delete or status column). One record per link, never one per user. With derived secrets (option B), add the app's bridge key to its server config instead (for example `GECKOGRAPH_BRIDGE_KEY`, 32 random bytes; never committed).
4. **Add the backend widget session route** (contract.md, "The backend creates a widget session"). It takes the user from the app's session, never from the request body, and names the bridge environment.
5. **Add the backend redeem route** that the frontend posts to. It takes the user from the app's session, never from the request body.
6. **Mount the panel** on a page of the part of the app the graph covers, signed in by that login, and register it as the panel URL (contract.md, "The page loads the panel"): load `https://app.geckograph.com/geckograph-embed.min.js`, call `GeckoGraph.mountMcpPanel` with the widget session and `onBridgeLinkRequest`, and refresh the widget session with `setWidgetSession` before it expires (it lasts 1 hour). That page must keep GeckoGraph's query parameters, also through the app's login redirect: a signed-out user signs in first and comes back to the same URL.
   If the app's pages carry GeckoGraph's chat widget, also hand the same widget session and `onBridgeLinkRequest` to it with `GeckoGraph.setMcpWidgetSession` on each page with the widget, refreshed the same way (contract.md, "The chat widget's MCP button"). Its MCP button is a GeckoGraph setting: build no button or second panel for it.
7. **Add `POST {sessionUrl}` and `POST {revokeUrl}`**, each behind the JWT check (verify-jwt.md). Read the raw body bytes before any JSON parsing.
8. **Write the tests** in testing.md, and run them.
9. **Register the three URLs** for one deployed environment in the GeckoGraph dashboard (the app's Session bridge tab; on the personal environment, `geckograph mcp local` registers local URLs instead, "Try it locally"): the session URL, the revoke URL and the panel URL. Each is `https`, has no query string or fragment, and is on the app's registrable domain (contract.md, section 3). A person registers them, as an owner or admin. Each environment (staging, production) has its own deployment, so its own URLs. Turn the bridge on in staging first.
10. **Check it end to end** with `geckograph mcp auth check` (below), or have a person click "Run the test" under "Test MCP auth" on the Session bridge tab. It runs the same check from their own signed-in browser.

## Try it locally

Before deploying, try the whole flow on the developer's machine, on their personal environment (the one `geckograph dev` opens). Nothing is set up in the dashboard for it: no verified domain, no `mcp:provision` key, no URL registration.

1. Check the tunnel with `geckograph dev --status`. When it is not running, start it yourself with `geckograph dev --start --local <host:port>`, pointed at the app's local server: it runs in the background, and `geckograph dev --stop` stops it. The status names the local app the tunnel points at: when that is not this server app's host and port, stop it and start it again with that one. Before the next step, run `geckograph dev --status` again until it says the tunnel is online. Only when your tool permissions refuse it, give the person that one complete command, with this repo's absolute path, to run in their own terminal.
2. Build steps 1 to 8 as above. The app reads its provisioning credential and environment name from its config, the same settings a deploy sets (Before you start).
3. Run `geckograph mcp local --dir <the server app's folder> --session-url http://localhost:<port>/… --revoke-url http://localhost:<port>/… --panel-url http://localhost:<port>/…`, with the app's own local routes. It writes the developer's dev token (as the provisioning credential) and the personal environment's name into that app's git-ignored local config, and registers the three URLs on the personal environment. Rerun it without the URLs to see what is registered, or with `--off` to turn the bridge off. Restart the app so it reads the new config.
4. Check it: `geckograph mcp auth check --app <slug> --env personal`, or use the panel or the chat widget's MCP button on the local page.

- GeckoGraph calls the session and revoke URLs through `geckograph dev`'s tunnel, so they work only while it runs, and reach the host and port `geckograph dev --local` points at: register them on that same port. The panel URL opens in the developer's own browser. Each JWT's audience is the URL of the endpoint it calls, as registered: the session URL on session calls and the revoke URL on revoke calls (`http://localhost:…` locally). Verify each against its own URL locally, and against the deployed URLs in each deployed environment.
- Agent tokens made on the personal environment are the developer's local test: they last at most 12 hours and work only while `geckograph dev` runs. End users of the deployed app use the deployed environments.
- `geckograph dev` renews the dev token while it runs and writes the new one into the same config. An app that reads its config only at startup keeps the old token: restart it when its provisioning calls start to answer 401 `invalid_key`. Never commit that file.
- On the personal environment the dev token makes the calls the local flow needs (widget sessions, bridge redeems, connect and claim, minting, listing and revoking its own tokens). An auth check through the provisioning API and a disconnect (`DELETE /end-users/…`) are refused there with 409 `dev_environment`: they take a provisioning key on a shared environment.

## Rules

- Bridge only the user kind the graph signs in as (step 1): its login, its user records, its sessions. Never another login's.
- Store a link's hash against the user of the **session** that posted the request, never against an id taken from a request body, a query string or the link request itself.
- Keep one hash **per link**, keyed by `linkId`. A user may hold more than one link (their own, and a check's beside it). A redeem never replaces or deletes another link's hash. (Option B keeps no hash: the secret is the HMAC of the `linkId` and the session's user, so each link is still bound to that user.)
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

- It needs the CLI signed in (`geckograph login`) as an owner or admin of the organization that owns the app. A member, an `mcp:provision` key and a CI token are refused. `--env` may be left out when exactly one environment has the bridge on; otherwise it lists them.
- It opens the app's panel page in the browser and prints the URL. The developer, signed in to the app as themselves, confirms there. The check makes a test link for that user, asks `sessionUrl` for a session, checks the user's own link still works, makes one read through MCP with that session (a GET tool that reads and needs no input, the first by name), revokes the test link, and checks that `sessionUrl` then answers `no_link`, that a bare `404` and a bad signature are read correctly.
- It prints each step with ✓ or ✗ and only its outcome (endpoint, status, error code, the read's tool), then a final line. Exit 0 means passed. The read shows – when it was not run (no such tool) or was inconclusive (a `404`, a `422`); neither fails the check.
- A person runs it at their desk, signed in to the app. It is not a CI step: run the tests in testing.md in CI.

| Step | ✗ usually means |
| --- | --- |
| `link_redeemed` | The frontend did not post the request, the backend redeem failed, or the `externalUserId` is not the signed-in user's. |
| `session` | `sessionUrl` did not answer `200` with `{ headers, cookies, expiresAt }`: a JWT check that refuses GeckoGraph's token, a hash stored wrongly, or a session the app's API does not accept. |
| `own_link_session` | The check's redeem replaced the user's own link's hash. Keep one hash per `linkId`. |
| `read` | The app refused the session `sessionUrl` returned (a `401`, a `403`, a redirect to sign-in), or did not answer. The session must carry what the app's API needs for that user: its headers and cookies. A bridge built for another kind of user than the graph's (step 1) fails here: the graph's routes refuse that login's session. |
| `revoke` | `revokeUrl` did not answer `204`. |
| `no_link_after_revoke` | The revoke did not delete the hash, or `sessionUrl` answers something other than `404 {"error":"no_link"}` for a gone link. |
| `bare_404` | `sessionUrl` answers a link it does not know with something other than `404 {"error":"no_link"}`. |
| `bad_signature` | `sessionUrl` accepted a token with a bad signature, or refused it with something other than `401`. |
