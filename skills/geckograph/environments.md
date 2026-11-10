# Shared environments: what each one uses, and who changes it

Read this when the person asks which version Staging or Live uses, why one did not change, or how to make one change. Shared environments (Staging, Live, and any others the team adds) change only when a person acts in the GeckoGraph dashboard. No command, CI step or API key changes them, so never try. Your part is to read the state with the CLI and tell the person exactly where to click.

## Words

- **Version**: a saved snapshot of what the chat assistant knows about the app: its features and the actions it can take. A new scan or a published edit makes a new one, such as v4.2.
- **Published**: finished and ready for environments to use. A draft is not published, so no environment uses it yet.
- **Tools number**: the number the app's code sends with the widget (`data-tools`) to say which actions that build was made with. Pages running older code get the version made for their number, so they keep working. Only `geckograph release` issues a new one.

## The three states

Every shared environment is in exactly one of these. An environment that follows your code is the exception: each page says which build it is, and the environment uses the version made for that build, so none of the three applies to it.

- **Following**: the environment switches to each new version automatically, as soon as it is published. Users' chat assistant uses the new version on their next message. New environments start here.
- **Approval**: new versions wait until someone on the team approves them. Until then the chat assistant keeps using the version it has now. Approval is one switch for the whole app, not per environment.
- **Held**: the environment stays on its current version, even when a new one is published, until someone presses Resume following. That happens after an edit is saved there, after a rollback, or when someone holds it. A hold is never permanent.

## Who does each action

A person does all of these in the dashboard. Give the page, the card and the button by name.

| Action | Where the person clicks | What happens |
| --- | --- | --- |
| Hold | Environments page, the environment's card, **Hold on vX** (or Versions page, the version's row, **Hold an environment on this version**) | The environment stays on that version until someone resumes following. |
| Resume following | Environments page, the held environment's card, **Resume following** | The hold ends. The environment switches to the newest published version now (or waits for approval, when Approval is on). |
| Approve | Environments page, the environment's card, **Review and approve** | The environment switches to the waiting version. Users get it from their next message. |
| Roll back | Environments page, the environment's card, History, **Roll back to vX** | The environment switches back to that older version and is held there. |
| Approval on or off | Environments page, top, **Approval for new versions: On / Off (all environments)** | On: no environment switches to a new version until someone approves it. Off: every environment that is not held switches to the newest version now. |

The dashboard asks the person to confirm each of these on a customer-facing environment such as Live.

## What you can do from the CLI

- Read: `geckograph list_versions` shows each version and which shared environment uses it now (`on Live`, `on Staging`). `geckograph read_knowledge_graph`, `geckograph list_features`, `geckograph show_feature --id <id> --env live` and `geckograph show_tool --tool <name> --env live` show what a version holds.
- Pick what you work on: `geckograph switch_app <slug>` and `geckograph switch_version <version>`.
- Your personal environment, which only you use: `geckograph dev` and `geckograph relearn`.
- A new tools number for changed code: `geckograph release --base <n>`, checked with `geckograph check --tools <n>`.
- Edits to a shared environment land in its draft, and a person saves them in the dashboard: `geckograph edit_feature --env staging --input <json>`, `geckograph content --env staging --input <json>`, `geckograph accept_scan_proposal --id <id> --env staging`.
- Checks: `geckograph validate --env staging --id <id>`, `geckograph list_validation_failures --env staging`, `geckograph rescan --env staging`.

## If the person wants…

| The person wants | Do this |
| --- | --- |
| Live to use the newest version | Run `geckograph list_versions`. Then tell them: on the Environments page, on Live's card, press **Review and approve** (Approval on) or **Resume following** (Live is held). If Live is Following it already switches on its own once the version is published. |
| Live to stop changing for now | Tell them: on the Environments page, on Live's card, press **Hold on vX**. |
| Live back on an older version | Tell them: on Live's card, open History and press **Roll back to vX**. Live is then held there. |
| New versions to wait for a person | Tell them: at the top of the Environments page, set **Approval for new versions** to On. |
| A change in the code to reach Staging | Open the pull request. After it merges and the new version is published, Staging switches on its own if it is Following; otherwise tell them which button above applies. |
| To know why Staging did not change | Run `geckograph list_versions`, then say which state it is in and which button changes it. |

## Messages the person may see

| Message | What it means, and what to say or do |
| --- | --- |
| "It's staying on its current version for now. There's no newer version for it to switch to yet." | Resume following found nothing newer. Publish a new version (or roll back to an older one), then press Resume following again. |
| "v3.8 was changed after it was published (or never was), so no environment can use it." | That version can't be used. The environment still uses its current version. Publish it again, or pick another version. |
| "No versions yet." | The first scan of the app hasn't finished. Ask the person to run it from the dashboard; it makes v1.0. |
| "Nothing is waiting for approval because Approval for new versions is off." | There is nothing to approve. Approval is off, so environments that are not held already switch on their own. |
| "There's no new version waiting for approval on tools number 0." | Publish a new version first. |
| "There's nothing on tools number 0 it can stay on right now. Pick a published version to hold it on." | Tell the person to pick a version in the hold dialog. |
| "Users can sign in, but the chat assistant can't take any other action until…" | The card names what is missing: an approval, a version published again, or a first version. Tell the person that step. |
| "Your personal environment always uses your own work in progress, so it can't be held or approved." | Hold, Resume following and Approve are for shared environments only. |
