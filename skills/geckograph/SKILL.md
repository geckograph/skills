---
name: geckograph
description: Keeps a customer's GeckoGraph graph in sync with their code. Use when adding, changing, or removing a feature, route, or API, before opening a pull request, or when GeckoGraph is mentioned.
---

# GeckoGraph

GeckoGraph holds a graph of the app's features and the actions (tools) that call its API. You keep it in sync by writing `geckograph.yml` and running the `geckograph` commands below.

## Guardrails

- Use only the `geckograph` commands this skill lists.
- The CLI reads the API address and key that `geckograph login` saved. Do not pass `--api` or `--api-key`.
- Never read `~/.config/geckograph/credentials.json` or any other credential or token file.
- Never call the GeckoGraph API directly, with `curl` or anything else.
- Never inspect the CLI binary, with `strings` or anything else.
- If a command is missing or fails, report it. Do not work around it.
- Never pass `--yes` to `geckograph init` or `geckograph contract pull` unless the person asked for the import.
- Never regenerate `geckograph.yml`. Edit your own `source: code` lines in it by hand.

## Install the CLI

If `geckograph` is not on PATH:

macOS and Linux:

```bash
curl -fsSL https://app.geckograph.com/cli/install.sh | bash
```

Windows:

```powershell
irm https://app.geckograph.com/cli/install.ps1 | iex
```

The Windows script checks the download against the published `SHA256SUMS`. The release is not signed.

Then run `geckograph login --no-browser` in the background. It prints a sign-in URL whether or not a browser opens: give that URL to the person to open on any device, and wait until it prints `Logged in to GeckoGraph.` (the link expires in 10 minutes). Never sign in for them.

## First run

1. If `.geckograph` is missing, run `geckograph init --app <slug>`. It links the repo and selects the app. Commit `.geckograph`. The CLI keeps its own state in `.geckograph.local`, which git ignores; never commit it.
2. When `geckograph.yml` is missing, init may offer to write it from the routes Live serves. That import is the person's decision: do not pass `--yes` unless they asked for it.
3. Init also offers to write a GeckoGraph block into `CLAUDE.md` and/or `AGENTS.md`. Without a terminal it writes nothing and prints how to accept. Ask the person; if they agree, run `geckograph init --app <slug> --agent-instructions`. `--yes` does not write the block.

## geckograph.yml

One entry per procedure the client calls. The file is the complete list: add, change or delete lines; never rewrite it with only the lines you touched. Relearn retires each action it built from a line that left the file (see Remove a feature).

```yaml
version: 1
contracts:
  - method: POST
    path: /api/invoices/:id/archive
    name: archive_invoice
    request:
      reason: string?
    response:
      archived: boolean
  - method: POST
    path: /api/trpc/invoices.archive
    binding: trpc
    request:
      id: string
```

- `method` is `GET`, `POST`, `PUT`, `PATCH`, or `DELETE`. `path` is relative and starts with `/`; a path parameter is `:name`. Relearn and `geckograph deployed` refuse a path with a host.
- `request` lists the fields the call sends: the JSON body for POST, PUT and PATCH, the query string for GET and DELETE. `response` lists the fields it returns. Types are `string`, `number`, `boolean`, `object`, `array`; `?` marks an optional field. One level deep.
- `name` (optional, snake_case) names the action relearn creates. If it is not snake_case or already taken, relearn keeps the generated name and says why.
- `binding: trpc`: one line per procedure, `/api/trpc/<router>.<procedure>`, never the catch-all `/api/trpc/[trpc]`. POST sends the input object as the body, GET sends it as `?input=<JSON>`. If the app's tRPC server uses `transformer: superjson`, the input goes as `{"json": …}`: relearn and `geckograph deployed` read that from the code, or write `transformer: superjson` on the line (`none` for plain). If the app's own tRPC calls are batched, relearn does not create the line and says why.
- `binding: graphql`: one line per endpoint, usually `POST /graphql` with request `query: string`, `variables: object?`, `operationName: string?`. Lines are keyed by method and path (`operation:` is only a label), so do not add a line per operation or per field.
- `source: code` is the default and marks your lines. `source: dashboard` lines were imported from Live by the person. Never edit or delete them. Only `geckograph contract pull` refreshes them, and only when the person asks.
- `ignore:` (optional, after `contracts:`) lists routes on disk that are not tools, one plain string each: `- POST /api/webhooks/stripe`. Relearn never makes them actions, also when the route is called through the dev tunnel. The GeckoGraph widget relay route is always ignored and needs no entry.
- No descriptions, guardrails, permissions, examples, or GeckoGraph ids.

## Auth

Never put tokens, cookies, API keys, or literal header values in `geckograph.yml`. Relearn copies auth and CSRF headers from an existing action on the same origin, as placeholders only. Deploy verification does not use a saved login for the app, and you never set one: `geckograph deployed` probes the environment origin once. A 401 or 403 there does not hold the move. That is not `geckograph login`, which signs the CLI in.

## The loop

Keep `geckograph dev --local <host:port>` running in the background, pointed at the app's local server (default `localhost:3000`). It opens your personal environment and holds the tunnel; do not wait for it to exit. Relearn needs it running. If it prints `already running`, one is already holding the tunnel: leave it and do not start another. `geckograph dev --stop` stops it.

### Add a feature

1. Run `geckograph list_features --json` and use the area that fits the request instead of creating one. Relearn files the feature under the area the line's path names (`/api/trpc/invoices.archive` -> Invoices), so put the route under that area's resource.
2. Implement the procedure and add its line to `geckograph.yml` by hand, in the same change.
3. With `geckograph dev` running, run `geckograph relearn`.
4. Reread the branch diff (see Before the commit).
5. Ask the person. Commit the code and the `geckograph.yml` diff only after they agree.
6. Do not run `geckograph deployed`. Open the pull request. CI runs `geckograph deployed` after the deploy, once that environment is serving this commit.

### Update a feature

- Change fields, path, or method by editing the line in place. To rename, edit `name:`.
- Editing a line's fields (added, removed, required or optional), binding or `name:` updates its action on your personal environment: relearn prints `Updated action: …`. A line that did not change is left alone.
- A new path with the same `name:` moves the action this personal environment created. A new path with another name is a new route: relearn creates its action and retires the old one if this environment created it.
- To change an endpoint Live already serves, leave its `source: dashboard` line alone. Add a code line for that route matching what it does now, run `geckograph relearn` (it records the line and changes nothing), then change the code and that line together. Deploy updates it on Live only when relearn rewrote it from the very line you deploy; otherwise Live records the line and changes nothing.
- Relearn never rewrites a login action, or an action it did not build whose request is not plain JSON: it prints `Not updated: <reason>`. Tell the person.
- A new method on the same path is a possible rename: relearn keeps the old action, creates none for the new line, and prints `Possible rename`. Tell the person.
- Then follow Add, steps 3 to 6. CI's `geckograph deployed` applies the same updates once that environment is serving the commit, also to an action from Live that relearn rewrote from the same line, and prints `Updated on <env>`. A change it cannot apply prints `Not updated on <env>: <name> (<reason>)`: tell the person. The name is the environment's (`Staging` or `Live`).

### Remove a feature

1. Delete the procedure and its `source: code` line.
2. Run `geckograph relearn`. It retires the action when this personal environment created it (`Removed action: …`), and hides a feature left with no active action (`Hidden feature: …`), which the agent no longer offers. A file with no line left retires nothing. Relearn does this for every line missing from the file, deleted on purpose or not: when it prints `Removed action` for a line you did not mean to remove, put the line back and run `geckograph relearn` again, and tell the person.
3. An action that came from Live stays. When the file holds imported lines, relearn lists it as `in Live but not in geckograph.yml` and prints a dashboard link. Tell the person to open it, select the tool, and choose Suppress.
4. Then follow Add, steps 4 to 6.

## What relearn does

- Reads the `source: code` lines of `geckograph.yml`. A dashboard line never creates an action. With only dashboard lines it prints `No code lines to relearn`.
- Creates, updates and retires actions on your personal environment only. It never changes Live or Staging.
- Without `geckograph.yml` it walks the code in this directory instead.
- Makes each new line's action the tool of a feature in the area its path names. When no area matches, it adds one named after the path's resource (`/api/reports/monthly` -> Reports) and prints `New feature area: <area>`. Each link prints `Feature: <feature> (<area>) uses <action>`. An action it created earlier that is no feature's tool is linked on the next relearn; `No feature: <reason>` when it could not be.
- Prints what it did: `Applied N new endpoints`, `Updated action`, `New feature area`, `Feature`, `Removed action`, `Hidden feature`, `Not created`, `Not updated` or `No feature: <reason>`, name notes, possible renames, and actions from Live that the file does not list.
- If relearn or `geckograph deployed` prints `TIMEOUT` (also after a 502, 503 or 504), the server may still be applying the change. Do not edit `geckograph.yml`; wait a minute and run the same command again. If it applies nothing, the first run finished: the dashboard shows what changed. A retry of `geckograph deployed` or `geckograph relearn` while that job is still running attaches and prints its report. A retry after the job is done starts another one.

## Before the commit

Reread the branch diff (`git diff <base>...HEAD` plus uncommitted changes) and confirm every route the branch created, changed (method, path, or parameters) or removed is reflected in `geckograph.yml`: a new route has its line, a changed route's line matches it, a removed route's line is gone. This is a review you do, not a command. A route the branch added that the agent should not call (login, auth, chat, admin, webhooks) goes under `ignore:`; do not add lines for routes the branch did not touch. Relearn and `geckograph deployed` refuse a file with an unknown `binding`, a path that names a host, or an `ignore:` entry that is not `METHOD /path`, and name each line.

## Deploy

Stop at the pull request. Do not run `geckograph deployed` from this checkout: that would publish actions for a commit the environment is not serving yet. CI runs `geckograph deployed` after the deploy, with the SHA that environment is running. It records the contract and prints `The environment moved.` or `The environment stayed.` with the reason (for example `verification_failed`, or `missing` when that environment does not exist), and exits 0 either way: staying is not a failure of the change. A shared environment moves only when its own read probe passes; the probe is one GET of the environment origin. A 401 or 403 means the server is there and does not hold the move. A route that needs a signed-in user is not called: it prints `Not verified on <env>: <name> (requires login)` (`notVerified` in `--json`) and never fails or holds the move. Line updates reach the environment with that move (see Update a feature). Each line's action takes the feature relearn gave it on the personal environment, area included: `New feature area on <env>: <area>` and `Feature on <env>: <feature> (<area>) uses <action>` (`Not on <env>: feature ...` when it stayed, `No feature: <reason>` when it could not link one; `linked` in `--json`).

## Do not

- Never use `geckograph scan` to create or refresh `geckograph.yml`. Scan only prints what the walker sees.
- Never regenerate `geckograph.yml`, and never edit or delete a `source: dashboard` line.
- Never pass `--yes` unless the person asked for the import.
- Never read credential files or call the GeckoGraph API directly.
- Never put secrets, tokens, cookies, or header values in `geckograph.yml`.
- Never write a catch-all `/api/trpc` line.
- Never run `geckograph deployed` before the environment is serving that commit. CI does that after the deploy. The pull request is the last step here.
