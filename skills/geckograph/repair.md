# Repairing a failing feature

When the person says yes to repair, your first words, before any command, are a few plain lines: what repair is (GeckoGraph tests each feature on the app's test account; for a feature that failed, the fault is in GeckoGraph's copy of it, not their app, so their code won't change; for one that isn't proven yet, the cause isn't known yet, and the repair finds out); how it goes (you fix each one on their personal environment, test it twice, then send the ones that pass to <env>'s draft, which they review and save in the dashboard); and that it takes a while, several minutes per feature. Say this even when the repair shares a turn with other work, such as a commit. Then post one short line as each feature finishes (passed, or why not).

## Why the fix is in GeckoGraph, not the app
A tool is a copy of a request the app's own screen sent during the scan, and the scan recorded the status the app
answered (`show_tool` shows both). When the app accepted that request and a validation fails, the test sent something different
from what the screen sent, asked for something the screen never offers, or picked a record the app refuses for this. The
request the screen sent is the specification: make the tool match it. When the scan's own call was refused too (the
app answered it with an error status), the graph is likely not the cause: test data or the tenant's setup is. Confirm
it in the route's code (what makes it refuse) before you say which, and cite it with that capture as the evidence.

`list_validation_failures` lists only failures that are GeckoGraph's own: every outcome it shows starts with `FAILED_`.
When the app itself failed, validation says so with its own outcome (`NOT_VALIDATED_APP_CALL_FAILED`: the screen's own call
failed in the scan the same way; `NOT_VALIDATED_APP_DROPS_FIELD`: the app ignored a field its own screen also lost). Those are
not validated, not failed, and they are not in the list.

The list holds validation results only. A tool that failed when a real user called it, in chat or over MCP, is not
in it: when the person reports one, find its feature (`list_features`) and validate it; a failure that validation also sees
is listed, with its evidence.

## Steps
1. List the failures and rescan flags on the environment (`--env`). A rescan flag (`held (rescan)`) is a feature a rescan
   added; it is served nowhere until it passes.
2. Read the failure: `show_validation_failure` gives the outcome, the test's ask, and each call with its arguments and the
   answer. Read the chat before deciding anything. A stored report is where you start, not a verdict: never call a
   feature missing test data, an app limit or one to switch off from it alone. Validate it again
   (`geckograph validate --env personal --id <id>`, after `geckograph dev --latest --from <env>` when `geckograph dev`
   says your personal copy is older), or cite the scan capture and code that settle it.
3. Read the tool: `show_tool` gives the request the app's screen sent in the scan and the status the app returned.
4. Compare the two. Typical differences, all fixed in GeckoGraph:
   | What you see | Fix |
   |---|---|
   | Our call left out a field the screen sends | `tool_input` with `change: add` for that field; for a tool built from `geckograph.yml`, its line |
   | Our call took a value from the wrong place (an old record, a guess) | `input_source` (the list tool and field it comes from), or `tool_input` with `change: user` when the person types it |
   | A write was refused before it was sent: no source for the record's current list | `input_source` that gives the input the record's current list (the read tool that lists it, and the field in its answer), and a `check` that reads it back |
   | The test picked a record or option the app refuses for this, and another one works (another feature passed on the same tool, or the same write passed on another record) | `test_hint` that says how to pick one that works, such as "Use a project that is still open; archived projects refuse new tasks." |
   | A read the test used to look up a value said there are none, but the record's own detail read has it | `input_source` from the detail read (the tool, and the field in its answer) |
   | The test asked for something the screen never offers | a `rule` that says what the screen allows |
   | The feature uses a tool that belongs to another feature | `bind` with `as: none`, or `move_tool` |
   | The tool's description or the feature's text misleads | `content`, or `rewrite` |
   | The tool itself has the wrong shape (an input missing, required when the screen leaves it out, or tied to a list when the person types it) | `tool_input` (`change`: `add`, `optional` or `user`) first; for a tool built from `geckograph.yml`, its line, then `relearn`, steps 5 and 6 on `--env personal` (`validate --env personal --id <id>`), and only then `check --tools <n>` and `release --base <n>` |
   Before you call it missing test data, look for a pass on the same tool (`show_tool` lists the features that use it;
   `geckograph show_feature --id <id> --env <env>` gives each one's last validation) and for a record or option that
   works: when one works, compare the failing call's inputs with the working one's. When the failing record is missing a
   value its own detail read has, the fix is an `input_source` (the table row above); otherwise the cause is which record
   the test picked, and the fix is a `test_hint`. When a read says
   there are none, confirm it lists that thing at all (in the route's code in this repo, or with another read, such as
   the record's own detail) before you believe it. Only when nothing works, say exactly which test data is missing and
   where in the app to add it, in your report to the person, with the evidence (the calls, the scan capture, the code).
5. Make one fix on your personal environment (`--env personal`; the edit is recorded), then validate that feature there (`validate --env personal --id <id>`), and try it in the local widget. A `geckograph.yml` line's fix needs its relearn first. One fix at a time, so each result says what the fix did.
6. Repeat until it passes twice in a row with the same fix; one pass out of two is a miss, not a repair. Stop when the same failure comes back with the same evidence as the try before (the same tool call, the same kind
   of argument and the same answer from the app; another tool, a new value instead of an existing one, or another
   answer is a new failure, not a repeat), or after 50 steps on one feature, and report. Before you call a write feature
   unrepairable, try the write route its screen uses (the feature's tools in `show_feature`, the request in
   `show_tool`), not only another route. A tool's wording changes every feature that uses the tool: after a wording
   edit, each of those features needs its two passes too (`geckograph validate --env personal --tool <tool>`), or
   promote sends none of them.
7. `geckograph promote --env <env>` replays your recorded edits into `<env>`'s draft. A feature's edits go together or not
   at all, and features that share an edit (a tool's wording, a moved tool) go together. It prints, per group of features,
   what it sent (with the command that validates it on the draft) or the one reason it did not send it: a feature not
   proven yet (with the command that validates it), the draft already changed that feature, or an edit that can never go
   as it is (its feature or rule is not in the draft, or the edit no longer applies there), with the way out. When your personal copy was made from
   another version than `<env>` serves, it says so first and carries no local pass. Validate each applied feature once
   on `<env>`: the save is blocked only if a feature that passed before now fails, or hasn't been tested on the draft since the change; a feature that wasn't passing before never blocks it, and the dashboard lists it as still not passing. A switch-off (`suppress`) lands in the draft and takes effect when a
   person saves it; a `hide` stays on your personal environment. A feature without two passes on your personal environment
   is listed as not proven locally and stays there, still to send, with every feature it shares an edit with; when your local app cannot prove it, repair it on
   `<env>`'s draft directly and say why.
8. When a change turns out wrong, or a promoted feature can't be proven on the draft and the person wants it out, put
   the feature back where the change is: `geckograph revert_feature --env personal --id <id>` for a repair not
   promoted yet, `--env <env>` for one in `<env>`'s draft. Run it with `--dry-run`, show the person what it will undo,
   and stop: end your turn and wait for their OK, even when they asked for the revert. Their ask is not the OK; the
   preview is what they approve. Then run it without `--dry-run`. Revert only what they asked for: never revert another
   copy (personal or a draft) on your own. Don't undo it edit by edit. When it says a tool is shared, ask the person
   whether to put the tool back for every feature that uses it (`--tools restore`) or keep it (`--tools keep`). A
   revert drops the repair: never use it only to get the draft saved.

## Edit kinds
Each `geckograph edit_feature --env <env> --input '<json>'` takes exactly one of these. A key a kind does not list is refused, and a refusal names the field and prints the shape. Optional keys: `goalExamples`, `title`, `field` on `check`, `type` and `in` on `tool_input` (`type` is needed with `add`), `on` on `ui_only` (`false` gives the feature back to the agent). Tool wording goes through `geckograph content --env <env> --input '{"tool":"<tool>","label":"<label>","description":"<text>","confirm":true}'`.
- `{"kind":"bind","id":"<feature id>","tool":"<tool>","as":"own|helper|none"}`
- `{"kind":"move_tool","id":"<feature id>","tool":"<tool>","name":"<new feature name>","description":"<text>","goalExamples":["<ask>"]}`
- `{"kind":"input_source","id":"<feature id>","input":"<input>","tool":"<list tool>","field":"<field in its answer>"}`
- `{"kind":"tool_input","id":"<feature id>","tool":"<tool>","input":"<input>","change":"optional|user|add","type":"string|number|boolean","in":"body|query"}`
- `{"kind":"check","id":"<feature id>","tool":"<read tool>","field":"<field>"}`
- `{"kind":"rewrite","id":"<feature id>","name":"<name>","description":"<text>","goalExamples":["<ask>"]}`
- `{"kind":"rule","id":"<feature id>","op":"add|update|delete","ruleId":"<rule id, for update and delete>","title":"<title>","text":"<text, for add and update>"}`
- `{"kind":"test_hint","id":"<feature id>","text":"<text>"}`
- `{"kind":"ui_only","id":"<feature id>","reason":"<why>","on":true|false}`
- `{"kind":"hide","id":"<feature id>","reason":"<why>"}`
- `{"kind":"suppress","id":"<feature id>","tool":"<tool>","reason":"<why>"}`
- `{"kind":"lift","id":"<feature id>","controlId":"<control id>"}`

## Never
- Never change the app's code to make it accept what our tool sent. If you find a real bug in the app (it crashes or
  answers wrongly on a request its own screen also sends), tell the person: what, where, and the evidence. Propose a
  change only if they ask, and only change it after they say yes to that change. If a change you made, in the graph or
  the app, turns out wrong, revert it; don't patch on top of it.
- Never call a result an app limit without the app's own evidence: its error on a request shaped like the screen's, or a
  refusal its own screen gives. A refusal from GeckoGraph's own checks, an error on arguments we built, or a missing tool
  is ours.
- Never give the agent more than the app's screens allow. If the screen hides or refuses an action, it stays refused: do not
  widen a binding or call a route the screen does not offer.
- A rule or description is served to every user of the app, so it must hold for any record: never name the test's own record or values in it. App-specific wording is what the graph is for. A `test_hint` is the exception: only validation reads it (its tester gets it as the feature's test data), never the agent, so it may say which records or values to use.
- Never hide, `ui_only` or `suppress` a feature just to empty the list. Hide one only for a reason you state, and tell the
  person what else it takes with it (suppress cascades).
- Never write a rule that refuses what the screen allows. A failure is not fixed by making the agent decline the ask.
- Real writes go only through the test account a person authorized for the tenant in the dashboard. Never mark a sign-in as a test tenant, or authorize a test account, yourself.
- Never count NOT VALIDATED as passed. Tell the person which features were proven and which were not, and why.
