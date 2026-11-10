# GeckoGraph skill

A skill other people install in their own repo. On first use it edits `AGENTS.md` or `CLAUDE.md`. A feature change is written into `geckograph.yml` by the agent (method, path, and fields). Scan does not author that file. With `geckograph dev` running, the agent runs `geckograph relearn`. Before the commit it rereads the branch diff and confirms every route the branch created, changed or removed is reflected in `geckograph.yml`. It then commits the code and the `geckograph.yml` diff and opens the pull request. A person changes what Staging and Live use in the GeckoGraph dashboard; `environments.md` beside the skill tells the agent where they click.

```bash
npx skills add geckograph/skills
```

The repo also holds `geckograph-mcp-auth`: the skill a SaaS app's coding agent builds the app's side of GeckoGraph's MCP session bridge from, so the app's end users sign GeckoGraph in through the app's own session. The same install adds it.

Claude Code loads this repo as a plugin from `.claude-plugin/plugin.json`. Cursor loads it from `.cursor-plugin/plugin.json`.

The CLI is a separate binary. macOS and Linux use `https://app.geckograph.com/cli/install.sh`. Windows uses `https://app.geckograph.com/cli/install.ps1`, which checks the download against the published `SHA256SUMS`. The release is not signed.
