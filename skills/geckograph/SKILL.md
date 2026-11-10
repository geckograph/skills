---
name: geckograph
description: Sets a repo up for GeckoGraph, keeps its GeckoGraph graph in sync with the code, and builds the app's GeckoGraph permissions endpoint. Use when asked to set up or work with GeckoGraph, when adding, changing, or removing a feature, route, or API, when asked to build or fix the GeckoGraph permissions endpoint or Permissions hook, before opening a pull request, or when GeckoGraph is mentioned.
---

# GeckoGraph

## How GeckoGraph works

- GeckoGraph holds a graph of this app's features and the actions (tools) an AI agent calls on the app's API. The app's users reach it through chat, the website widget and MCP. An app that serves its end users through MCP builds its part of their sign-in (the session bridge) from the `geckograph-mcp-auth` skill: install it as this one installs outside a repo, `npx skills add geckograph/skills`, which adds it beside this one, and check it with `geckograph mcp auth check --app <slug> --env <environment>`.
- The graph comes from GeckoGraph's scan of the running app, which the person runs in the GeckoGraph dashboard. You never build it, and never write lines for what it already holds.
- `geckograph.yml` in this repo lists the app's routes. `geckograph init` writes the ones GeckoGraph already knows as `source: dashboard` lines. After that, a change that adds, changes or removes a route edits its line in the same commit. That is the only time you write a line.
- Shared environments (production, staging, and so on) change only when a person acts in the GeckoGraph dashboard; no command, CI step or API key changes them. Each is Following, Approval or Held: `environments.md` beside this skill says what each means and where the person clicks. Your personal environment is yours: `geckograph dev` and `geckograph relearn` change only it.

**With no specific change to make, setup is the whole task.** Run Setup below, then ask the person what they want to change. Do not explore, scan or map the app's API.

## Guardrails

- Use only the `geckograph` commands this skill lists.
- The CLI reads the API address and key that `geckograph login` saved. Do not pass `--api` or `--api-key`.
- Never read `~/.config/geckograph/credentials.json` or any other credential or token file.
- Never call the GeckoGraph API directly, with `curl` or anything else.
- Never inspect the CLI binary, with `strings` or anything else.
- If a command is missing, run `geckograph update` and try again. If it is still missing, or a command fails, report it. Do not work around it.
- Never pass `--yes` to `geckograph contract pull` unless the person asked for it. Never pass `--yes` to `geckograph validate` or `geckograph rescan` unless the person said yes to that run.
- Never regenerate `geckograph.yml`. Edit your own `source: code` lines in it by hand.

## Setup

1. If `geckograph` is not on PATH, install it. macOS and Linux: `curl -fsSL https://app.geckograph.com/cli/install.sh | bash`. Windows: `irm https://app.geckograph.com/cli/install.ps1 | iex` (it checks the download against the published `SHA256SUMS`; the release is not signed).
2. Whenever a command prints `A newer GeckoGraph CLI is available`, run `geckograph update`, then run the command again. A fresh install is already current.
3. If a command says to log in, run `geckograph login --no-browser` in the background. It prints a sign-in URL whether or not a browser opens: give that URL to the person to open on any device, and wait until it prints `Logged in to GeckoGraph.` (the link expires in 10 minutes). Never sign in for them.
4. From the repo root, run `geckograph init`. It links the app, writes `geckograph.yml`, installs this skill into the repo, and writes a GeckoGraph block into `CLAUDE.md` or `AGENTS.md`. It asks nothing. If it lists several apps, ask the person which one this repo is, then run `geckograph init --app <slug>`.
5. Ask the person which apps and pages get the widget, then set it up (see The widget).
6. Show the person what init and you wrote (`git status`) and commit it only after they agree. Never commit `.geckograph.local`; git ignores it.
7. Stop, and ask the person what they want to change.

## The widget

The widget is GeckoGraph's chat on the app's own pages, one script tag, `data-app` being the linked app: `<script async src="https://app.geckograph.com/geckograph-embed.min.js" data-app="<slug>" data-env-key="…" data-dev-token="…" data-build="…" data-tools="…"></script>`. A repo can hold many apps and pages, and only the person knows which should have it. **Ask the person which apps and pages should get the widget; do not guess.** For each app they choose, run `geckograph widget_config --dir <app folder>`, then add the snippet in the setup commit to the entry pages they named (layout, master page or `index.html`).

- `data-env-key` names one environment: your personal one on your machine, Staging's or Live's once deployed. `data-dev-token` is the developer's own, from their sign-in: it lasts 12 hours, only works on a local page, and is never committed. `data-build` is the commit the build came from (the pipeline stamps `$(Build.SourceVersion)` or `GITHUB_SHA`); no `%VITE_GIT_SHA%` on a stack that is not Vite. `data-tools` is the tools number: which tools this build of the app was made with, so every page gets the graph tested with its own code, during a rollout or after a rollback too; a page without it is served tools 0, and only `geckograph release` issues a new one. When you change tools (a route and its line in `geckograph.yml`), run `geckograph relearn` and test, then `geckograph release --base <the number the app's config holds now, 0 if none>`; it prints the new number (`--json` gives `toolsNumber`) and edits no file. Put that number in the app's committed configuration, in the form the stack uses for build-time settings that ship with the code (never a local or git-ignored file, an environment variable set on one machine, or a value a pipeline injects), make the embed tag render it as `data-tools`, never a literal, and commit it with the code and the `geckograph.yml` change it was released for. When two branches both released, the number conflicts on merge: run `geckograph release` once on the merged code and commit the new number. Before each commit and before `geckograph release`, run `geckograph check --tools <the number the app's config holds now, 0 if none>`. It reads `geckograph.yml`, the tools number and `data-tools`, and edits no file. Exit 0: passed. Exit 1: fix each problem it lists (each names its line or file), then run it again. Exit 6: `geckograph.yml` changed since that number was released: run `geckograph release --base <that number>`, put the number it prints in the config, commit it with the yml, and run `geckograph check` again.
- The values come from untracked local config, never from a committed file. `geckograph dev` keeps your personal environment's key and dev token in `.geckograph.local`; `geckograph widget_config --dir <app folder>` writes them into that app's local config git ignores (`.env.development.local` for a Vite or Create React App build, which a production build never reads; `.env.local` for Next.js; `appsettings.Local.json`; `Web.Local.config`) and prints the names it used and what the app still needs: on .NET Framework add `file="Web.Local.config"` to Web.config's `<appSettings>`; on ASP.NET Core load `appsettings.Local.json` as an optional source. It writes nothing when git does not ignore that file, and never edits `.gitignore`. A deploy sets Staging's and Live's key. Never put a key or token in a committed file. For Staging's and Live's keys, run `geckograph widget_key --env staging` and `geckograph widget_key --env live` (add `--json` for a script), and put each key into that deploy's app setting or pipeline variable, or tell the person where to put them when you cannot reach the deploy config. A widget key is publishable (it is in the page) and works only from its environment's allowed domains.
- Each value comes from configuration in the stack's own form, never a literal. A server-rendered page renders the app setting: `process.env` for Node, `GeckoGraph:EnvKey` and `GeckoGraph:DevToken` in ASP.NET Core, `GeckoGraphEnvKey` and `GeckoGraphDevToken` in `ConfigurationManager.AppSettings` on .NET Framework. A bundled app reads a build variable (`import.meta.env.VITE_GECKOGRAPH_ENV_KEY`). A static page has neither: add what fills the values (a build step or a small server route) and tell the person what you added.
- When the app's login is an HttpOnly cookie (ASP.NET, NextAuth), the widget cannot send it, so the app needs the relay: copy the relay for its stack from `relay/` beside this skill (`aspnetcore`, `aspnet-framework`, `nextjs`) and add `data-base-url="/api/geckograph" data-discovery-url="/api/geckograph"` to the snippet.

## geckograph.yml

One entry per procedure the client calls. The file is the complete list: add, change or delete lines; never rewrite it with only the lines you touched. Relearn retires each action it built from a line that left the file (see Remove a feature).

```yaml
version: 1
contracts:
  - method: POST
    path: /api/invoices/:id/archive
    name: archive_invoice
    description: Archives a sent or paid invoice so it leaves the open list. Drafts cannot be archived.
    request:
      reason: string?
    response:
      archived: boolean
    notes:
      request:
        reason: Shown to the customer on the archived invoice.
  - method: POST
    path: /api/trpc/invoices.archive
    binding: trpc
    request:
      id: string
```

- `method` is `GET`, `POST`, `PUT`, `PATCH`, or `DELETE`. `path` is relative and starts with `/`; a path parameter is `:name`. Relearn refuses a path with a host.
- `request` lists the fields the call sends: the JSON body for POST, PUT and PATCH, the query string for GET and DELETE. `response` lists the fields it returns. Types are `string`, `number`, `boolean`, `object`, `array`; `?` marks an optional field. One level deep.
- `name` (optional, snake_case) names the action relearn creates. If it is not snake_case or already taken, relearn keeps the generated name and says why.
- `binding: trpc`: one line per procedure, `/api/trpc/<router>.<procedure>`, never the catch-all `/api/trpc/[trpc]`. POST sends the input object as the body, GET sends it as `?input=<JSON>`. If the app's tRPC server uses `transformer: superjson`, the input goes as `{"json": …}`: relearn reads that from the code, or write `transformer: superjson` on the line (`none` for plain). If the app's own tRPC calls are batched, relearn does not create the line and says why.
- `binding: graphql`: one line per endpoint, usually `POST /graphql` with request `query: string`, `variables: object?`, `operationName: string?`. Lines are keyed by method and path (`operation:` is only a label), so do not add a line per operation or per field.
- `source: code` is the default and marks your lines. `source: dashboard` lines were imported by `geckograph init` from the graph GeckoGraph already has. Never edit or delete them. Only `geckograph contract pull` refreshes them, and only when the person asks.
- `ignore:` (optional, after `contracts:`) lists routes on disk that are not tools, one plain string each: `- POST /api/webhooks/stripe`. Relearn never makes them actions, also when the route is called through the dev tunnel. The GeckoGraph widget relay route is always ignored and needs no entry.
- `description` (optional, one line) says what the call does and when a person would use it, in their words, from the code you just wrote. `notes:` (optional) holds one note per field, under `request:` or `response:`: what the field means, and an allowed value or a rule only when the code enforces it. Write both for every code line you add or change; a line without them gets a thin tool. Never on a `source: dashboard` line.
- No guardrails, permissions, examples, or GeckoGraph ids.
- **Auth and permissions.** Never put tokens, cookies, API keys, or literal header values in `geckograph.yml`. Relearn copies auth and CSRF headers from an existing action on the same origin, as placeholders only. To build or change the app's permissions endpoint (the Permissions hook), read `permissions.md` beside this skill and follow it; when you add or change a route and the app has that endpoint, update its answer for the route in the same change.

## The loop

Keep `geckograph dev --local <host:port>` running in the background, pointed at the app's local server (default `localhost:3000`). It opens your personal environment and holds the tunnel; do not wait for it to exit. Relearn needs it running. If it prints `already running`, one is already holding the tunnel: leave it and do not start another. `geckograph dev --stop` stops it.

### Add a feature

1. Run `geckograph list_features --json` and use the area that fits the request instead of creating one. Relearn files the feature under the area the line's path names (`/api/trpc/invoices.archive` -> Invoices), so put the route under that area's resource.
2. Implement the procedure and add its line to `geckograph.yml` by hand, in the same change.
3. With `geckograph dev` running, run `geckograph relearn`.
4. Reread the branch diff (see Before the commit).
5. Ask the person. Commit the code and the `geckograph.yml` diff only after they agree.
6. Do not try to change Staging or Live. Open the pull request: that is the last step here.

### Update a feature

- Change fields, path, or method by editing the line in place. To rename, edit `name:`.
- Editing a line's fields (added, removed, required or optional), binding or `name:` updates its action on your personal environment: relearn prints `Updated action: …`. A line that did not change is left alone.
- A new path with the same `name:` moves the action this personal environment created. A new path with another name is a new route: relearn creates its action and retires the old one if this environment created it.
- To change an endpoint Live already serves, leave its `source: dashboard` line alone. Add a code line for that route matching what it does now, run `geckograph relearn` (it records the line and changes nothing), then change the code and that line together. Staging and Live keep what they use until their state lets a new version in (`environments.md` beside this skill).
- Relearn never rewrites a login action, or an action it did not build whose request is not plain JSON: it prints `Not updated: <reason>`. Tell the person.
- A new method on the same path is a possible rename: relearn keeps the old action, creates none for the new line, and prints `Possible rename`. Tell the person.
- Then follow Add, steps 3 to 6. Staging and Live keep what they use until their state lets a new version in (`environments.md` beside this skill).

### Remove a feature

1. Delete the procedure and its `source: code` line.
2. Run `geckograph relearn`. It retires the action when this personal environment created it (`Removed action: …`), and hides a feature left with no active action (`Hidden feature: …`), which the agent no longer offers. A file with no line left retires nothing. Relearn does this for every line missing from the file, deleted on purpose or not: when it prints `Removed action` for a line you did not mean to remove, put the line back and run `geckograph relearn` again, and tell the person.
3. An action that came from Live stays. When the file holds imported lines, relearn lists it as `in Live but not in geckograph.yml` and prints a dashboard link. Tell the person to open it, select the tool, and choose Suppress.
4. Then follow Add, steps 4 to 6.

### Repair a failing feature, or a rescan flag
Name the environment on every command with `--env` (normally `staging`), and features by the id the commands print (`--id`). List `geckograph list_validation_failures --env staging` or `geckograph list_scan_flags --env staging`, then read one: `geckograph show_validation_failure --id <id> --env staging`, `geckograph show_scan_flag --id <id>`, `geckograph show_feature --id <id> --env staging`, `geckograph show_tool --tool <name> --env staging`. `held (rescan)` means a rescan added it and it is served nowhere until it passes. Read the app's code behind the failing tool's route to find why it fails, then fix where the fault is: the app's code; the tool's shape (its `geckograph.yml` line, `geckograph relearn`, `geckograph check --tools <n>`, then `geckograph release --base <n>` as The widget says); or the graph with one `geckograph edit_feature --env staging --input '{"kind":"…","id":"…",…}'` (`bind` with `as` own, helper or none; `move_tool` with `tool`, `name`, `description`; `input_source` with `input`, `tool`, `field`; `check` with `tool`; `rewrite`; `rule` with `op` and `text`; `ui_only`, `hide` or `suppress` with a `reason`), `geckograph content --env staging --input '{"tool":"…","description":"…"}'`, `geckograph accept_scan_proposal --id <id> --env staging`, `geckograph dismiss_scan_proposal --id <id>` or `geckograph ack_scan_flag --id <id> --flag <flag>`. A feature built from a `geckograph.yml` line takes binding and description changes in the line only. Prove it with `geckograph validate --env staging --id <id>`; pass `--yes` only after the person said yes to real writes. It writes only through a sign-in marked a test tenant's; otherwise write features come back not validated (no test-tenant login), which is not a failure. Repeat until it passes. Stop and report when a fix leaves the same failure with the same evidence as the try before, and never take more than 50 steps on one feature. Then tell the person the draft is ready to save in the dashboard (only a person saves Staging or Live, after the features you changed passed), and what you changed in the app's code.

## What relearn does

- Reads the `source: code` lines of `geckograph.yml`. A dashboard line never creates an action. With only dashboard lines it prints `No code lines to relearn`.
- Creates, updates and retires actions on your personal environment only. It never changes Live or Staging.
- Without `geckograph.yml` it refuses: run `geckograph init` first.
- Makes each new line's action the tool of a feature in the area its path names. When no area matches, it adds one named after the path's resource (`/api/reports/monthly` -> Reports) and prints `New feature area: <area>`. Each link prints `Feature: <feature> (<area>) uses <action>`. An action it created earlier that is no feature's tool is linked on the next relearn; `No feature: <reason>` when it could not be.
- Prints what it did: `Applied N new endpoints`, `Updated action`, `New feature area`, `Feature`, `Removed action`, `Hidden feature`, `Not created`, `Not updated` or `No feature: <reason>`, name notes, possible renames, and actions from Live that the file does not list.
- If relearn prints `TIMEOUT` (also after a 502, 503 or 504), the server may still be applying the change. Do not edit `geckograph.yml`; wait a minute and run the same command again. If it applies nothing, the first run finished: the dashboard shows what changed. A retry of `geckograph relearn` while that job is still running attaches and prints its report. A retry after the job is done starts another one.

## Before the commit

Reread the branch diff (`git diff <base>...HEAD` plus uncommitted changes) and confirm every route the branch created, changed (method, path, or parameters) or removed is reflected in `geckograph.yml`: a new route has its line, a changed route's line matches it, a removed route's line is gone. This is a review you do, not a command. A route the branch added that the agent should not call (login, auth, chat, admin, webhooks) goes under `ignore:`; do not add lines for routes the branch did not touch. Relearn refuses a file with an unknown `binding`, a path that names a host, or an `ignore:` entry that is not `METHOD /path`, and names each line.

## After the pull request

Stop at the pull request. There is no deploy command and no CI step: a person changes what Staging and Live use in the GeckoGraph dashboard.

## Do not

- Never use `geckograph scan` to create or refresh `geckograph.yml`. Scan only prints what the walker sees.
- Never regenerate `geckograph.yml`, and never edit or delete a `source: dashboard` line.
- Never read credential files or call the GeckoGraph API directly.
- Never put secrets, tokens, cookies, or header values in `geckograph.yml`.
- Never write a catch-all `/api/trpc` line.
- Never try to change Staging or Live: a person does that in the dashboard. The pull request is the last step here.
