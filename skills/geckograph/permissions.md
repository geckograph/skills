# The GeckoGraph permissions endpoint

Read this when the person asks you to build, change or fix the app's GeckoGraph permissions endpoint (the Permissions hook), or when a route you add or change needs the endpoint to answer for it. The rest of the GeckoGraph skill still applies.

## What it is

GeckoGraph's agent acts in the app as the signed-in user. Before it offers anything, GeckoGraph's server asks the app which API routes that user may call. The app answers from the rules it already applies: guards, roles, custom roles, permission tables, plans, tenant settings, feature flags, and rules the UI applies. GeckoGraph keeps no copy of those rules. It hides every action whose route the app did not allow, and refuses any call to one.

You build one endpoint in the app that gives that answer. GeckoGraph defines its contract. Do not point GeckoGraph at a permissions, `/me` or menu route the app already has, even one that looks close: build this one, on top of the logic the app already uses.

A person sets the endpoint's path on the environment's Permissions hook in the GeckoGraph dashboard, presses Test, and turns the hook on. You never turn it on, and nothing you write in the repo turns it on.

## The contract

GeckoGraph's server calls the endpoint once per conversation (and, when the hook's **When to check** is **Before every tool call**, again before each action with only that action's route):

```http
POST /geckograph/permissions/check
Content-Type: application/json
Authorization: Bearer <the user's token>      (when the app signs in with a token)
Cookie: <the user's cookies>                  (when the app signs in with a cookie, through the relay)

{ "routes": ["GET /api/campaigns", "POST /api/campaigns", "GET /api/campaigns/:id", "POST /api/reports/export"] }
```

It answers `200` with the routes this user may call:

```json
{ "allowed": ["GET /api/campaigns", "GET /api/campaigns/:id"], "unknown": ["POST /api/reports/export"] }
```

- **Route keys.** Each key is the method in capitals, one space, and the path: no host, no query string, no trailing slash, every placeholder written `:id` (`{id}`, `[id]`, `{id:int}` and `:campaignId` all arrive as `:id`, and so does an all-digit segment). Answer with the keys exactly as you received them.
- **`allowed`**: the routes this user may call. Required, an array of strings, and `[]` is a valid answer (a signed-in user who may call nothing). A route you received and left out of `allowed` is not callable.
- **`unknown`**: the routes the endpoint does not recognise. Optional. They are not callable either; GeckoGraph lists them on the hook's coverage view so someone adds them. Never put an unrecognised route in `allowed`, and never put a key in both lists.
- **`401`** when the request carries no valid session. The agent then tells the user their session expired and to reload or sign in again in the app. Any other non-2xx status, a redirect, a timeout, or a body that is not that object is a failed check: the agent offers nothing but sign-in and says it could not confirm the user's access.
- **No user id.** The endpoint works out the user and the tenant from the session on the request, the same way the app's API does. It takes no user id, email or tenant id from the body: a request that named the user would let a caller ask about someone else.
- **Answer per route, not per record.** "May this user edit campaigns at all?" Rules about a specific record ("only campaigns in your region", "only your own submissions") stay with the API, as they are today.
- **Fast.** GeckoGraph waits for the hook's timeout, 5 seconds by default and 10 at most, and one request can carry a few hundred routes. Load the user's grants, the tenant's settings and the flags once per request, then decide each route in memory. No query per route.
- **Read-only.** The endpoint changes nothing and returns nothing but the two lists: no permission ids, role names, flag names or reasons.

### Which routes arrive

GeckoGraph asks about the routes of the actions in the version of the app's graph that the environment serves: one key per method and path those actions call, not the lines of `geckograph.yml`. Sign-in actions are never asked about: they stay callable. Each key is the action's template, already in route-key form: a placeholder, a numeric segment and a UUID arrive as `:id`, and a slug the scan captured arrives as written (`GET /api/campaigns/spring-sale`). A tRPC batch action is asked about once per procedure it names. The endpoint answers each key by looking it up, as sent, in its own table (see Matching keys), and answers `unknown` for anything else.

When the app's API is served under a prefix the code's routes don't include (a sub-path, a virtual directory, a gateway), the person sets that prefix as the hook's **Base path**, and keys arrive without it.

### Where the path comes from

The person types the endpoint's path into the Permissions hook in the dashboard (for example `/geckograph/permissions/check`). It is a path on the environment's own URL, never another host, and GeckoGraph never follows a redirect on it. Do not add a `permissions:` block to `geckograph.yml`: the path comes from the hook. Tell the person the path you chose so they can enter it.

The endpoint is not a tool. Add it to `geckograph.yml` under `ignore:` (`- POST /geckograph/permissions/check`), never as a contract line.

## Build it

1. **Find how the app decides access today.** Read the guard on each route (an ASP.NET `[Authorize(Policy = …)]`, an Express or Next.js middleware, a decorator, a check inside the handler) and the data those guards read: role and permission tables, records that tie a permission to a page or menu item, tenant settings, plan entitlements, feature flags. Read how the UI decides what to show, too. Do not assume a single role column.
2. **List the route keys the agent can call**, exactly as GeckoGraph sends them, with the check each one needs (see Matching keys). Reuse the app's own policy functions and loaders. Do not copy permission, role, flag or setting data into code or a file: read what the app reads at request time, so an admin's change applies on the next check with no deploy.
3. **Apply the UI's rule where the API has none.** When the API accepts a call and only the UI hides the button or blocks the form, the endpoint is the only gate the agent meets. Answer with the rule the UI applies, never looser.
4. **Combine every condition.** A route that needs a user permission and a tenant setting (or a plan, or a flag) is allowed only when all of them hold. That includes every check the route inherits: a controller's or router's guard as well as the handler's own, and the app's default or fallback rule for routes that declare none.
   **Decide each route as the user its own authentication would see.** When the app's API routes authenticate differently (some by session cookie, some by bearer token), answer each route with the identity that route's scheme produces from this request, which carries the same credentials the agent's call will. Never decide a bearer-only route with the cookie session's user, or the other way round.
5. **Answer `unknown` for anything else.** A route your table does not list is `unknown`, never `allowed`. Default to deny. The sample looks keys up by exact equality and never matches patterns, so a key it was not written for is always `unknown`.
6. **Return `401`, not a redirect, when there is no valid session**, and make sure the app's CSRF or antiforgery check does not reject this POST (see Common mistakes).
7. **Describe record rules with `scopedBy`.** When a route also checks the record (owner, team, area, tenant), answer for the route as a whole and add one sentence to that route's `geckograph.yml` line, for example `scopedBy: Only campaigns in the areas the user manages.` **In flight:** relearn accepts the field, but GeckoGraph does not read it yet, so the agent does not mention it. It never changes the decision.
8. **Keep it current.** Whenever you add or change a route, update the endpoint's table in the same change, the same way you update its `geckograph.yml` line.
9. **Test it** (see Test it), then tell the person the path and what is left for them: enter the path on the Permissions hook, press Test, read the coverage, turn it on.

## Matching keys

GeckoGraph asks about the route keys of the served version's actions, each already in one fixed form: the method in capitals, one space, the path with no host, query string or trailing slash, and every placeholder, numeric and UUID segment written `:id`. So the endpoint does not resolve keys against the app's router. It keeps a table keyed by those exact strings and follows one rule:

**A key is answered by the rule listed for exactly that key, character for character. Any key not in the table is `unknown`, never `allowed`.**

There is no pattern or wildcard matching, no route precedence, no letter-case folding and no decoding: a key that differs in any character from every table entry (a slug the table does not list, an encoded segment, another letter case) is `unknown`; to answer a slug key, list it as sent. The sample checks the table when it loads and refuses an entry GeckoGraph would never send, so a mistyped entry fails at startup instead of never matching. An entry is one of `GET`, `POST`, `PUT`, `PATCH`, `DELETE`, one space, then `/` alone or `/`-separated segments, each either `:id` or text as GeckoGraph sends it: percent-decoded (`/api/café`, not `/api/caf%C3%A9`), in the app's letter case, and never empty, all ASCII digits, a UUID, starting with `:`, or containing `/`, `?`, `{`, `[` or whitespace (GeckoGraph sends those as `:id`, sends no key for them, or cannot read an answer for them).

**When a `:id` key shares its place with a static route.** An action whose key is `GET /api/campaigns/:id` runs with whatever value the agent passes. If the app also routes `/api/campaigns/export` on its own, the value `export` reaches that route. So the rule for `GET /api/campaigns/:id` must hold only when every rule such a value could reach also holds: require the sibling's check too, or leave the `:id` key out so it stays `unknown`. A sibling that sends data out or changes something can make the placeholder route too strict for some users; that is the safe direction.

**Where the keys come from.** The authoritative list is the one GeckoGraph sends. The sample answers every key it does not list as `unknown`, so a Test on the Permissions hook against an empty table lists every key of the served version under **Unknown routes**, and the hook's coverage view keeps listing the keys the table still misses. Read the app's code for the check each one needs. A framework's own route list can help draft the table, but it is incomplete and is never used to answer a key: Next.js's `routes-manifest.json` misses rewrites and middleware, an Express app's router stacks miss mount prefixes and RegExp routes, and ASP.NET's `EndpointDataSource` or `ApiExplorer` miss what other handlers serve.

The TypeScript below is the shared part of the Next.js and Express examples. Replace `AccessContext`, `loadAccessContext` and the `can` functions with the app's own types, loaders and policy checks.

```ts
// geckograph-permissions.ts
export type RouteRule<C> = { route: string; can: (ctx: C) => boolean };

const METHODS = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE'];
const UUID = /^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$/;

/** A segment GeckoGraph sends: `:id`, or decoded text it did not turn into `:id`. */
function keySegment(segment: string): boolean {
  if (segment === ':id') return true;
  // No whitespace (\s): GeckoGraph can't read back an answer for a key that contains it.
  return segment.length > 0 && !/[/?{[]/.test(segment) && !/\s/.test(segment) && !segment.startsWith(':')
    && !/^[0-9]+$/.test(segment) && !UUID.test(segment);
}

/** A route key in the form GeckoGraph sends: method, one space, then "/" or "/"-separated segments. */
function isRouteKey(key: string): boolean {
  const at = key.indexOf(' ');
  if (at < 0 || !METHODS.includes(key.slice(0, at))) return false;
  const path = key.slice(at + 1);
  return path === '/' || (path.startsWith('/') && path.slice(1).split('/').every(keySegment));
}

/** The table by key. Throws when it loads on an entry GeckoGraph would never send, or on a key listed twice. */
export function ruleTable<C>(rules: RouteRule<C>[]): Map<string, RouteRule<C>> {
  const table = new Map<string, RouteRule<C>>();
  for (const rule of rules) {
    if (!isRouteKey(rule.route)) throw new Error(`Not a route key GeckoGraph sends: ${rule.route}`);
    if (table.has(rule.route)) throw new Error(`Listed twice: ${rule.route}`);
    table.set(rule.route, rule);
  }
  return table;
}

/** Each key is answered by the rule listed for exactly that key; any other key is unknown. */
export function decide<C>(routes: unknown, table: Map<string, RouteRule<C>>, ctx: C): { allowed: string[]; unknown: string[] } {
  const allowed: string[] = [];
  const unknown: string[] = [];
  for (const route of Array.isArray(routes) ? routes : []) {
    if (typeof route !== 'string') continue;
    const rule = table.get(route);
    if (!rule) unknown.push(route);
    else if (rule.can(ctx)) allowed.push(route); // the key exactly as received
  }
  return { allowed, unknown };
}
```

```ts
// The app's table: every route key the agent can call, with the check the app already makes for it.
type AccessContext = { permissions: Set<string>; tenantFlags: Set<string> };

const canRead = (c: AccessContext) => c.permissions.has('campaigns.read');
const canExport = (c: AccessContext) => c.permissions.has('reports.export') && c.tenantFlags.has('reports-export'); // both must hold

export const TABLE = ruleTable<AccessContext>([
  { route: 'GET /api/campaigns', can: canRead },
  { route: 'POST /api/campaigns', can: (c) => c.permissions.has('campaigns.write') },
  { route: 'GET /api/campaigns/export', can: canExport },
  // The app also routes /api/campaigns/export, which an :id value of "export" reaches: require its check too.
  { route: 'GET /api/campaigns/:id', can: (c) => canRead(c) && canExport(c) },
  { route: 'POST /api/reports/export', can: canExport },
]);
```

### Next.js (App Router)

```ts
// app/geckograph/permissions/check/route.ts
import { NextResponse } from 'next/server';
import { decide, TABLE } from '@/lib/geckograph-permissions';
import { getSessionUser, loadAccessContext } from '@/lib/auth'; // the app's own

export async function POST(req: Request) {
  const user = await getSessionUser(req); // the same session lookup the app's API routes use
  if (!user) return NextResponse.json({ error: 'unauthorized' }, { status: 401 });
  const body = await req.json().catch(() => null);
  if (!body || !Array.isArray(body.routes)) return NextResponse.json({ error: 'routes must be an array' }, { status: 400 });
  const ctx = await loadAccessContext(user); // grants, tenant settings and flags, loaded once
  return NextResponse.json(decide(body.routes, TABLE, ctx));
}
```

If `middleware.ts` (`proxy.ts` in Next.js 16) redirects signed-out requests to a login page, don't exclude `/geckograph/` from its matcher: this path should still pass the middleware's other checks (tenant resolution, rate limits, headers). Answer `401` for it there instead of redirecting:

```ts
// middleware.ts: the app's own checks stay; only the signed-out answer differs for this path.
if (!session) {
  if (!req.nextUrl.pathname.startsWith('/geckograph/')) return NextResponse.redirect(new URL('/login', req.url));
  // An app whose API also takes bearer tokens lets a request carrying one through: the handler checks it.
  if (!req.headers.get('authorization')?.startsWith('Bearer ')) return NextResponse.json({ error: 'unauthorized' }, { status: 401 });
}
```

### Express

```ts
import express from 'express';
import { decide, TABLE } from './geckograph-permissions';
import { loadAccessContext, requireApiAuth } from './auth'; // the app's own: requireApiAuth is the guard its API routes use

export const geckographPermissions = express.Router();

// Mount before any CSRF middleware, or exclude this path from it. The same authentication guard the API routes use,
// so this answer is about the user those routes would see; it must answer 401 (not redirect) when no one is signed in.
geckographPermissions.post('/geckograph/permissions/check', express.json({ limit: '1mb' }), requireApiAuth, async (req, res) => {
  const user = req.user; // set by that guard, as on the API routes
  if (!user) return res.status(401).json({ error: 'unauthorized' });
  if (!Array.isArray(req.body?.routes)) return res.status(400).json({ error: 'routes must be an array' });
  const ctx = await loadAccessContext(user);
  res.json(decide(req.body.routes, TABLE, ctx));
});
```

Apply the guard the app's API routes actually use (a `passport.authenticate(...)`, a session check, a JWT check), not only a test for `req.user`: a user the API would refuse must get no answer here either.

### Next.js and Express: API routes with different authentication

When some API routes authenticate with the session cookie and others with a bearer token, decide each route with the identity its own scheme gives this request, which carries the same credentials the agent's call will. Look up each identity the way those routes do, and let each rule read the one its route uses; answer `401` only when no scheme yields a user.

```ts
type Identities = { session: AccessContext | null; bearer: AccessContext | null };

export const MIXED_TABLE = ruleTable<Identities>([
  { route: 'GET /api/campaigns', can: (i) => !!i.session && canRead(i.session) },              // cookie-session route
  { route: 'GET /api/campaigns/export', can: (i) => !!i.bearer && canExport(i.bearer) },       // bearer-only route
]);

// In the handler: the lookups each kind of route uses. unauthorized() and answer() stand for the stack's own
// response: NextResponse.json(…, { status: 401 }) and NextResponse.json(…), or res.status(401).json(…) and res.json(…).
const sessionUser = await getSessionUser(req); // as the cookie routes authenticate
const bearerUser = await getBearerUser(req);   // as the bearer routes authenticate
if (!sessionUser && !bearerUser) return unauthorized();
const identities: Identities = {
  session: sessionUser ? await loadAccessContext(sessionUser) : null,
  bearer: bearerUser ? await loadAccessContext(bearerUser) : null,
};
return answer(decide(body.routes, MIXED_TABLE, identities));
```

### ASP.NET Core

Answer each key the way ASP.NET Core's authorization middleware answers the API route, so the endpoint and the API cannot disagree. Each row copies the route's `[Authorize]` attributes as they are, the controller's and the action's alike, with their policies, roles and authentication schemes. The sample then combines them into one policy with `AuthorizationPolicy.CombineAsync`, the function the middleware uses: a bare `[Authorize]` is the app's default policy, and a row with no attributes gets the app's fallback policy, or is `unknown` when the app sets none, because then the API checks nothing and only a rule the UI applies could answer. It authenticates the policy's own schemes on this request (or the app's default scheme when the policy names none), so a bearer-only route is decided with the bearer identity and never with the cookie session's, then authorizes. Every policy name must be one the app registers (`CombineAsync` throws on an unknown one, which fails the whole check). An `[AllowAnonymous]` route has no API check: leave it out, or give it a policy that states the UI's rule. Add a policy for any route only the UI guards.

**When the sample can't tell which identity the API would see, the route is `unknown`.** A policy scheme (`AddPolicyScheme`) picks its target from the request, often from its path, and this endpoint's path is not the route's: when the scheme a route would authenticate with is one, name the concrete scheme on that row instead (`AuthenticationSchemes = "Bearer"`). The same goes for any scheme whose options forward by request (`ForwardDefaultSelector`), which the sample can't detect. A route that names no scheme, in an app with no default authentication scheme, is `unknown` too, rather than failing the whole check.

**Copy what endpoint conventions and MVC authorization filters require.** `app.MapControllers().RequireAuthorization("Policy")`, or `RequireAuthorization` on a route group, adds an `[Authorize]` to every endpoint it covers: add `new AuthorizeAttribute("Policy")` to those rows (`RequireAuthorization()` with no policy is a bare `new AuthorizeAttribute()`), and leave out a route a convention marks `AllowAnonymous()` unless a policy states the UI's rule. A global `AuthorizeFilter` in `MvcOptions.Filters` is a second check the API makes after endpoint authorization: copy its attributes into `GlobalFilter` (every `AuthorizeFilter` the app adds, together) and mark the rows it covers (controller actions, not minimal APIs). That check doesn't use the filter's policy alone. MVC combines it with the route's own `[Authorize]` attributes into one effective policy, schemes included, so a filter that names no scheme authenticates a bearer-only route with the bearer identity. The sample decides that combined policy per route, as a check of its own after the route's policy, the same way it decides an `AlsoRequires` route. A custom authorization filter (`IAuthorizationFilter`, `IAsyncAuthorizationFilter`) can't be evaluated here: leave the routes it guards out of the table.

A route with `AlsoRequires` is allowed only when it and every route it reaches allow, not allowed when any of them denies, and `unknown` otherwise.

**A policy that needs the resource can't be checked here.** The sample authorizes with no resource. The API's middleware passes the request's `HttpContext`, and a controller may pass the record (`AuthorizeAsync(User, record, policy)`). A handler that reads either (`AuthorizationHandler<TRequirement, TResource>`, route values, query, headers) has nothing real to read here, and its answer is meaningless. Leave such a route out of the table, so it stays `unknown`, unless the app has a check for the route that does not need them; the per-record check stays with the API.

```csharp
// using Microsoft.AspNetCore.Authentication; using Microsoft.AspNetCore.Authorization.Policy; (AuthenticateAsync, IPolicyEvaluator, PolicySchemeHandler)
[ApiController]
[Route("geckograph/permissions")]
[Authorize(AuthenticationSchemes = "Cookies,Bearer")] // every scheme the app's API accepts; 401 here, not a redirect (below)
[IgnoreAntiforgeryToken]    // a read-only answer; antiforgery would reject GeckoGraph's server-to-server POST
public sealed class GeckoGraphPermissionsController(
    IAuthorizationService authz, IAuthorizationPolicyProvider policies, IPolicyEvaluator evaluator,
    IAuthenticationSchemeProvider schemes) : ControllerBase
{
    public sealed record CheckRequest(List<string>? Routes);

    // Every route key the agent can call, exactly as GeckoGraph sends it, with the [Authorize] attributes on that API
    // route (controller's and action's, copied as they are), the keys of any other route a value of its :id reaches,
    // and whether the app's global AuthorizeFilter also runs on it (controller actions do; minimal APIs don't).
    private static readonly (string Route, AuthorizeAttribute[] Authorize, string[] AlsoRequires, bool GlobalFilterRuns)[] Rules =
    {
        ("GET /api/campaigns", new[] { new AuthorizeAttribute("Campaigns.Read") }, new string[0], true),
        // Controller [Authorize(Roles = "Admin")], action [Authorize("Campaigns.Write")]: both apply.
        ("POST /api/campaigns", new[] { new AuthorizeAttribute { Roles = "Admin" }, new AuthorizeAttribute("Campaigns.Write") }, new string[0], true),
        // A route that takes bearer tokens only: the scheme comes with its attribute.
        ("GET /api/campaigns/export", new[] { new AuthorizeAttribute("Reports.Export") { AuthenticationSchemes = "Bearer" } }, new string[0], true),
        // An :id value of "export" reaches the export route: it must pass too, with its own scheme.
        ("GET /api/campaigns/:id", new[] { new AuthorizeAttribute("Campaigns.Read") }, new[] { "GET /api/campaigns/export" }, true),
        // A bare [Authorize]: the app's default policy.
        ("GET /api/me/settings", new[] { new AuthorizeAttribute() }, new string[0], true),
        // No [Authorize] on the route: the app's fallback policy, or unknown when it sets none.
        ("GET /api/help", new AuthorizeAttribute[0], new string[0], false),
    };

    // The app's global AuthorizeFilter (MvcOptions.Filters), copied as attributes; empty when it adds none.
    private static readonly AuthorizeAttribute[] GlobalFilter = new AuthorizeAttribute[0];
    private static readonly Dictionary<string, string> Table = Linked(RouteKeys.Table(Rules.Select((r, i) => (r.Route, i.ToString()))));

    /// Refuses, when the table loads, an AlsoRequires key that is not in the table.
    private static Dictionary<string, string> Linked(Dictionary<string, string> table) =>
        Rules.SelectMany(r => r.AlsoRequires).All(table.ContainsKey)
            ? table : throw new InvalidOperationException("AlsoRequires names a route key that is not in the table");

    [HttpPost("check")]
    public async Task<IActionResult> Check([FromBody] CheckRequest request)
    {
        if (request.Routes is null) return BadRequest();
        var decided = new Dictionary<string, bool?>();   // one evaluation per route, however often it is required
        var allowed = new List<string>();
        var unknown = new List<string>();
        foreach (var route in request.Routes)
        {
            if (!RouteKeys.TryBest(route, Table, out var index)) { unknown.Add(route); continue; }
            var rule = Rules[int.Parse(index)];
            // The route and every route it reaches: any denial denies, otherwise any unknown is unknown.
            var decisions = new List<bool?> { await DecideAsync(rule.Route, rule.Authorize, decided) };
            foreach (var other in rule.AlsoRequires)
            {
                var reached = Rules[int.Parse(Table[other])];
                decisions.Add(await DecideAsync(reached.Route, reached.Authorize, decided));
                if (reached.GlobalFilterRuns && GlobalFilter.Length > 0) decisions.Add(await FilterAsync(reached.Route, reached.Authorize, decided));
            }
            if (rule.GlobalFilterRuns && GlobalFilter.Length > 0) decisions.Add(await FilterAsync(rule.Route, rule.Authorize, decided));
            if (decisions.Contains(false)) continue;
            if (decisions.Contains(null)) unknown.Add(route);
            else allowed.Add(route);
        }
        return Ok(new { allowed, unknown });
    }

    /// One API route as the authorization middleware decides it, once per check.
    private async Task<bool?> DecideAsync(string route, AuthorizeAttribute[] authorize, Dictionary<string, bool?> decided)
    {
        if (!decided.TryGetValue(route, out var result)) decided[route] = result = await DecideOnceAsync(authorize);
        return result;
    }

    /// The global AuthorizeFilter on one route, a check of its own after endpoint authorization. MVC's effective filter
    /// policy is the filter's policy combined with the route's [Authorize] attributes, schemes included.
    private Task<bool?> FilterAsync(string route, AuthorizeAttribute[] authorize, Dictionary<string, bool?> decided) =>
        DecideAsync(route + " (global AuthorizeFilter)", GlobalFilter.Concat(authorize).ToArray(), decided);

    /// Null (unknown) when the API checks nothing there, or when this endpoint can't tell which identity it would see.
    private async Task<bool?> DecideOnceAsync(AuthorizeAttribute[] authorize)
    {
        var policy = authorize.Length > 0
            ? await AuthorizationPolicy.CombineAsync(policies, authorize)   // policies, roles, schemes; bare [Authorize] = default
            : await policies.GetFallbackPolicyAsync();                     // no [Authorize]: the fallback policy, if any
        if (policy is null) return null;
        // The schemes the middleware would authenticate: the route's own, else the app's default (none: unknown).
        var names = policy.AuthenticationSchemes.Count > 0
            ? policy.AuthenticationSchemes.ToList()
            : (await schemes.GetDefaultAuthenticateSchemeAsync()) is { } byDefault ? new List<string> { byDefault.Name } : new List<string>();
        if (names.Count == 0) return null;
        foreach (var name in names)
        {
            // A policy scheme picks its target from the request, and this request's path is not the route's.
            var scheme = await schemes.GetSchemeAsync(name);
            if (scheme is null || scheme.HandlerType == typeof(PolicySchemeHandler)) return null;
        }
        // HttpContext.User is this route's identity while it is checked (a handler may read it there), then restored, so
        // no route's identity leaks into the next. IPolicyEvaluator.AuthenticateAsync also sets it.
        var original = HttpContext.User;
        try
        {
            var authenticated = policy.AuthenticationSchemes.Count > 0
                ? await evaluator.AuthenticateAsync(policy, HttpContext)
                : await HttpContext.AuthenticateAsync(names[0]);
            if (!authenticated.Succeeded || authenticated.Principal is null) return false;
            HttpContext.User = authenticated.Principal;
            return (await authz.AuthorizeAsync(authenticated.Principal, null, policy)).Succeeded;
        }
        finally
        {
            HttpContext.User = original;
        }
    }
}

// Plain C# 7.3, with no null in its signatures, so it builds warning-free with nullable reference types on or off,
// and on .NET Framework 4.7+, where the handler below reuses it. Needs `using System.Text.RegularExpressions;`.
static class RouteKeys
{
    private static readonly string[] Methods = { "GET", "POST", "PUT", "PATCH", "DELETE" };
    /// Whitespace as JavaScript's \s has it: GeckoGraph can't read back an answer for a key that contains it.
    private static readonly Regex Space = new Regex(@"[\t\n\v\f\r \u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]");
    private static readonly Regex Uuid = new Regex(@"^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\z");
    private static readonly Regex Digits = new Regex(@"^[0-9]+\z");

    /// A segment GeckoGraph sends: `:id`, or decoded text it did not turn into `:id`.
    private static bool KeySegment(string segment) =>
        segment == ":id"
        || (segment.Length > 0 && segment.IndexOfAny(new[] { '/', '?', '{', '[' }) < 0 && !Space.IsMatch(segment)
            && segment[0] != ':' && !Digits.IsMatch(segment) && !Uuid.IsMatch(segment));

    /// A route key in the form GeckoGraph sends: method, one space, then "/" or "/"-separated segments.
    private static bool IsRouteKey(string key)
    {
        var at = key.IndexOf(' ');
        if (at < 0 || Array.IndexOf(Methods, key.Substring(0, at)) < 0) return false;
        var path = key.Substring(at + 1);
        return path == "/" || (path.Length > 0 && path[0] == '/' && path.Substring(1).Split('/').All(KeySegment));
    }

    /// The table by key. Throws when it loads on an entry GeckoGraph would never send, or on a key listed twice.
    public static Dictionary<string, string> Table(IEnumerable<(string Route, string Value)> rules)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (route, value) in rules)
        {
            if (!IsRouteKey(route)) throw new ArgumentException("Not a route key GeckoGraph sends: " + route);
            if (table.ContainsKey(route)) throw new ArgumentException("Listed twice: " + route);
            table.Add(route, value);
        }
        return table;
    }

    /// The value listed for exactly `key`, character for character, as in the TypeScript above. False, so `unknown`, otherwise.
    public static bool TryBest(string key, Dictionary<string, string> table, out string value)
    {
        if (table.TryGetValue(key, out var found) && found != null) { value = found; return true; }
        value = "";
        return false;
    }
}
```

Cookie authentication redirects a signed-out request to the login page by default, which GeckoGraph reads as a failed check. Answer `401` on this path instead:

```csharp
builder.Services.ConfigureApplicationCookie(options =>   // or .AddCookie(options => …)
{
    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/geckograph")) context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        else context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
});
```

### ASP.NET (.NET Framework)

An `HttpTaskAsyncHandler`, like the relay beside this skill, fits Web Forms, MVC and Web API apps alike. `RouteKeys` is the class from the ASP.NET Core example. Each `Can` must check everything the API route requires: the controller's and the action's `[Authorize(Roles = …, Users = …)]`, and any global `AuthorizeAttribute` (`GlobalFilters.Filters` for MVC, `GlobalConfiguration.Configuration.Filters` for Web API). A Web API route that accepts bearer tokens only is decided with the identity the OWIN bearer middleware gives this request (`context.GetOwinContext().Authentication.AuthenticateAsync("Bearer")`, from `Microsoft.Owin.Host.SystemWeb`), never with the Forms or cookie user; if the app can't read that identity here, leave those routes out of the table. The cookie user also comes from its own scheme, never from `context.User` as such: an active OWIN bearer middleware can put the bearer identity there. With Forms authentication, it is `context.User` only when its identity is the `FormsIdentity`; with OWIN cookie authentication, set `OwinCookieType` and the sample reads it like the bearer one. An app without the OWIN pipeline has no bearer identity to read here, so its bearer routes are `unknown`, and its cookie routes still work.

```csharp
// using Microsoft.Owin; using System.Security.Claims; using System.Security.Principal; using System.Web.Security;
public sealed class GeckoGraphPermissionsHandler : HttpTaskAsyncHandler
{
    // The schemes the app's API uses. "" for Forms authentication; else the OWIN cookie type, e.g. "ApplicationCookie".
    private static readonly string OwinCookieType = "";
    private static readonly string BearerType = "Bearer";

    // Every route key the agent can call, whether it is bearer-only, and everything its API route requires.
    private static readonly (string Route, bool Bearer, Func<AccessContext, bool> Can)[] Rules =
    {
        // Controller [Authorize(Roles = "Staff")] plus the action's own check.
        ("GET /api/campaigns", false, c => c.User.IsInRole("Staff") && c.Has("campaigns.read")),
        ("POST /api/campaigns", false, c => c.User.IsInRole("Staff") && c.Has("campaigns.write")),
        // A bearer-only Web API route: decided with the bearer identity.
        ("GET /api/campaigns/export", true, c => c.Has("reports.export")),
    };
    private static readonly Dictionary<string, string> Table = RouteKeys.Table(Rules.Select((r, i) => (r.Route, i.ToString())));

    public override async Task ProcessRequestAsync(HttpContext context)
    {
        context.Response.SuppressFormsAuthenticationRedirect = true; // 401, never a 302 to the login page
        // Each identity from its own scheme. OWIN only when the app runs it: GetOwinContext() throws without it.
        var owin = context.Items["owin.Environment"] != null ? context.GetOwinContext() : null;
        var bearer = owin != null ? await owin.Authentication.AuthenticateAsync(BearerType) : null;
        var bearerUser = bearer != null && bearer.Identity != null && bearer.Identity.IsAuthenticated ? new ClaimsPrincipal(bearer.Identity) : null;
        var cookie = owin != null && OwinCookieType.Length > 0 ? await owin.Authentication.AuthenticateAsync(OwinCookieType) : null;
        // Forms: context.User only when its identity is the Forms one, never a bearer identity OWIN put there.
        var cookieUser = OwinCookieType.Length == 0
            ? (context.User != null && context.User.Identity is FormsIdentity forms && forms.IsAuthenticated ? context.User : null)
            : (cookie != null && cookie.Identity != null && cookie.Identity.IsAuthenticated ? new ClaimsPrincipal(cookie.Identity) : null);
        if (cookieUser == null && bearerUser == null) { context.Response.StatusCode = 401; return; }
        string body;
        using (var reader = new StreamReader(context.Request.InputStream)) body = await reader.ReadToEndAsync();
        var json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        var parsed = json.Deserialize<Dictionary<string, object>>(body);
        var list = parsed != null && parsed.TryGetValue("routes", out var raw) ? raw as ArrayList : null;
        if (list == null) { context.Response.StatusCode = 400; return; }
        var routes = list.OfType<string>().ToList();
        // The app's own: grants, tenant settings, flags, loaded once per identity.
        var cookieCtx = cookieUser != null ? AccessContext.Load(cookieUser) : null;
        var bearerCtx = bearerUser != null ? AccessContext.Load(bearerUser) : null;
        var allowed = new List<string>();
        var unknown = new List<string>();
        foreach (var route in routes)
        {
            if (!RouteKeys.TryBest(route, Table, out var index)) { unknown.Add(route); continue; }
            var rule = Rules[int.Parse(index)];
            // Without OWIN there is no bearer identity to read: unknown, not denied.
            if (rule.Bearer && owin == null) { unknown.Add(route); continue; }
            var ctx = rule.Bearer ? bearerCtx : cookieCtx;
            if (ctx != null && rule.Can(ctx)) allowed.Add(route);
        }
        context.Response.ContentType = "application/json";
        context.Response.Write(json.Serialize(new { allowed, unknown }));
    }
}
```

Register it in Web.config under `<system.webServer><handlers>` as `<add name="GeckoGraphPermissions" verb="POST" path="geckograph/permissions/check" type="YourApp.GeckoGraphPermissionsHandler" />`, and make sure no `<location>` rule redirects that path to the login page.

## Signed-list mode (in flight)

For an app that cannot expose an endpoint, the spec has a fallback: the app's server signs the list of allowed routes, the widget carries the signed list, and GeckoGraph verifies it. GeckoGraph's verification is built, but **the widget cannot carry the signed list yet**. Build the endpoint. Build signed-list mode only when the person asks for it, and tell them it cannot be used until the widget carries the list.

When asked, the token is an HS256 JWT signed by the app's **server**, never the browser:

- `allowedRoutes`: the route keys this user may call, in the shape above. Sign every key from the endpoint's table that the user's checks allow.
- `exp`: required, at most 15 minutes ahead. A token valid for longer is refused.
- `cred`: the lowercase hex SHA-256 of the one session value the app authenticates with: by default the token the widget is given with `setAuth({ token })`, exactly as given (no `Bearer ` prefix unless the app put one there). When the hook's **Session value** names a cookie or a header, hash that cookie's or header's raw value instead, never the whole Cookie header.
- The key is the hook's **Token secret**, used as given: its UTF-8 text is the HMAC key. Do not base64-decode it. It comes from the app's server configuration, never a committed file or the browser. After a rotation, tokens signed with the old secret verify for 15 more minutes.

```ts
import { createHash } from 'node:crypto';
import jwt from 'jsonwebtoken';

export function signAllowedRoutes(allowedRoutes: string[], sessionValue: string): string {
  const cred = createHash('sha256').update(sessionValue).digest('hex');
  return jwt.sign({ allowedRoutes, cred }, process.env.GECKOGRAPH_PERMISSIONS_SECRET!, { algorithm: 'HS256', expiresIn: '10m' });
}
```

Re-sign before `exp`, and whenever the user's session or role changes.

## Common mistakes

- **Redirecting to the login page instead of answering `401`.** Cookie auth in ASP.NET, Forms authentication and many middlewares do this by default; GeckoGraph does not follow redirects and reads one as a failed check for every user.
- **`403` for a signed-in user with no access.** That is a failed check ("access couldn't be confirmed"). Answer `200` with `"allowed": []`.
- **CSRF or antiforgery rejecting the POST.** GeckoGraph calls from its server with the user's session and no CSRF token. Exclude this read-only route from the check.
- **Allowing what it doesn't recognise.** A default-allow, a catch-all `allowed: routes`, or echoing every key back hands every user every action. Unrecognised routes are `unknown`.
- **Matching keys by pattern.** Resolving a key against the app's routes with wildcards, precedence, case folding or decoding answers keys the table was not written for. Look each key up exactly; anything not listed is `unknown`. Route lists from the framework are drafting aids, never the answer.
- **A `:id` rule looser than its static siblings.** When the app routes `/api/campaigns/export` beside `/api/campaigns/:id`, the value `export` reaches the export route, so `GET /api/campaigns/:id` must also require the export check, or stay out of the table.
- **Looser than the UI.** When only the UI hides an action, the endpoint is the only gate. Apply the UI's rule.
- **Reading a user id, email or tenant id from the body**, or trusting `context.user` from the widget. The session decides who is asking.
- **Copying the rules.** A hard-coded role-to-route list, a JSON file of permissions, or a copy of which roles or tenants have a flag on goes stale as soon as an admin changes a role or a flag. Naming the check, as the sample's `c.tenantFlags.has('reports-export')` does, is fine; the values it reads must come from the app's data at request time, through the app's own policy checks.
- **Adding CORS for this route.** GeckoGraph calls it from its server, never from a browser, so it needs no CORS. Never give it credentialed or origin-reflecting CORS (`Access-Control-Allow-Credentials: true` with the request's `Origin` echoed back): any website could then read a signed-in user's permissions.
- **One query per route.** Hundreds of routes times a database round trip blows the timeout. Load once, decide in memory.
- **Reusing an existing permissions or menu endpoint**, or wrapping the answer (`{ "data": { "allowed": … } }`), or returning permission ids instead of route keys. The body must be exactly `{ allowed, unknown }` with the keys you were sent.
- **Answering only for the routes in `geckograph.yml`.** The routes sent come from the actions in the app's graph, including ones the scan found that the file doesn't list. Cover every API route the agent can call; anything else is `unknown`.
- **Leaving the endpoint out of `ignore:`**, or adding it as a contract line, which would make it a tool.
- **Putting the path in `geckograph.yml`.** The path is set on the hook in the dashboard. Tell the person what it is.
- **Signed list:** base64-decoding the secret, hashing `Bearer <token>` instead of the token, hashing the whole Cookie header, signing in the browser, or an `exp` more than 15 minutes out.

## Test it

1. **Unit-test the endpoint** the way the app tests its other routes: a user with full access gets every known route in `allowed`; a limited user gets the subset the UI shows them; an unknown route lands in `unknown`; a route that needs a tenant setting is refused when the setting is off; no session answers `401`; `routes: []` answers `{ "allowed": [], "unknown": [] }`.
2. **Call it locally** with a real session copied from a signed-in browser, for example `curl -i -X POST http://localhost:3000/geckograph/permissions/check -H 'Content-Type: application/json' -H 'Cookie: <session cookie>' -d '{"routes":["GET /api/campaigns","POST /api/campaigns","GET /api/nope"]}'`. Expect `200`, the right `allowed`, and `GET /api/nope` in `unknown`. Never commit or log the session.
3. **Run `geckograph relearn`** with `geckograph dev` running, so the routes you added or changed are actions on your personal environment, and the hook asks about them there.
4. **The Test button is the person's step.** Tell them to sign in to the app on the GeckoGraph dashboard, open **Hooks**, pick the environment (their personal one first, then Staging), enter the path, and press **Test**. Test calls the endpoint with their own sign-in and shows the HTTP status, the latency, the reason when it failed, the allowed and unknown routes, and the features they would be offered (marked view only when they can read but not change). **Coverage** lists graph actions with no matching route and routes answered `unknown`. To see a narrower role, they sign in to the app as a user with that role and press Test again.
5. **Reading a failed Test:** "HTTP 401: the endpoint refused the session" is a missing or unrecognised session (or a missing `[Authorize]`-style setup); "redirected, and redirects are not followed" is the login redirect above; "The answer is not { allowed, unknown }" is the body shape; "No answer within the timeout" is a slow endpoint or a query per route; "The endpoint could not be reached" is a wrong environment URL or a route that is not deployed there.
6. Stop there. The person reviews the endpoint in the pull request and turns the hook on, one environment at a time.
