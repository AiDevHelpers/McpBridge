# Script Helper

Runs allowlisted debug scripts elevated, without a UAC prompt per script. One elevated broker
is started with one UAC prompt. Unelevated callers send requests through a per-user queue, and
the broker runs only scripts that were approved for exactly the bytes it runs.

## Parts

| Executable | Runs as | Purpose |
|---|---|---|
| `elev.exe` | unelevated | The one helper exe. Runs approved scripts (`run`) and reads status (`verify`, `repo-scan`). Setup verbs that need administrator rights relaunch it elevated once, with one UAC prompt. |
| `scripthelper-broker.exe` | elevated, started hidden | Answers requests from the queue, heartbeat, stop, audit. |

All shared data lives under `%ProgramData%\AiDevHelpers\script-helper`, which Administrators
and SYSTEM control. Users can read it but not change it. Per-user queue and results folders are
owner-only.

## Project manifest: `.elevated.json`

Put this in the project root. Only listed scripts can run, and only with listed arguments.

```json
{
  "scripts": [
    { "path": "scripts/debug/attach.ps1", "args": ["-Port", "*"] },
    { "path": "scripts/debug/*.ps1" }
  ]
}
```

- `path` is relative to the project root. `*` matches within one folder only.
- No `args` means no arguments are allowed. `args` lists one pattern per argument, in order.
- `*` never matches quotes, `;`, `|`, `&`, `$`, backticks or line breaks.
- Any edit to the manifest needs re-approval, because its hash is pinned.

## Setup (once per project)

Run from any shell. The setup commands that need administrator rights relaunch `elev` elevated
once, with one UAC prompt, and the confirmations are answered in that elevated window:

```
elev register C:\path\to\repo
elev approve C:\path\to\repo scripts/debug/attach.ps1
```

`approve` shows the script path, every file it pulls in, the fingerprint and the allowlist hash,
then waits for `y`. The approval is refused if the script or manifest changes later.

For a caller that asks the person itself (for example an AI that shows the prompt in chat):

```
elev approve C:\path\to\repo scripts/debug/attach.ps1 --prepare
elev approve C:\path\to\repo scripts/debug/attach.ps1 --confirm <fingerprint> <allowlistHash>
```

`--confirm` writes the approval only if the script and manifest still hash to the values the
person was shown.

## Verify, scan and allow-all

```
elev verify C:\path\to\repo
elev review C:\path\to\repo
elev scan-launch C:\path\to\repo
elev allow-all C:\path\to\repo
elev allow-script C:\path\to\repo scripts/debug/attach.ps1 -Port 5000
```

- `verify` is read only. It prints the status as JSON (exit 0 all approved, 1 some are not, 2 no
  answer) and never starts a broker. The MCP server runs it when a repo with an `.elevated.json`
  is opened in an IDE and pushes a warning naming the unapproved files.
- `review` lists every file the repo's `.elevated.json` allowlist lists with its approval status for this machine: Approved, Unsigned
  (never approved), Changed (edited since approval) or AllowlistChanged (the team edited the allowlist). It then asks per file whether to
  approve the ones that are not. A teammate can update the committed lists freely; each user re-approves on their own machine.
- `scan-launch` reads `Properties/launchSettings.json`, `.vscode/launch.json` and `.vscode/tasks.json` for scripts they run (`.ps1`, `.cmd`,
  `.bat`, `.sh` inside the repo) and asks per script whether to add it to `.elevated.json`. Adding changes the allowlist, so run `review`
  afterwards.
- `allow-all` is for a registered repo whose `.aidevhelpers/settings.json` contains `"allowAll": true`. It asks two separate confirmations
  (the second repeats the repo path), then writes a signed approval to the admin-only data root, bound to the project, the checkout and the
  hash of `settings.json`. While the approval matches, the broker runs any script inside the repo without the allowlist or per-script
  approval; the owner and inside-the-repo checks still apply. Editing `settings.json` revokes it. The gitignored per-user
  `.aidevhelpers/user.json` only points at the approval. A repo file alone, or a global `allowAll`, grants nothing.
- `allow-script` records a user allow for one script and its exact arguments, without changing the repo. See User allows.

## Repo setup, rules, terminals and tools (unelevated `elev` verbs)

These run locally, need no broker and no elevation, and ask from the console, never from arguments.

- `elev repo-scan <repo>` prints, as JSON, projects and launch scripts that need administrator rights, the repo's CI branches and checks, instruction files, hooks and what is already recorded. It writes nothing.
- `elev repo-setup <repo>` asks per project whether to list it in `.aidevhelpers/elevation.json` (committed), once whether this repo should always launch elevated (`user.json`, per user), and whether to record the CI branches and checks as protected branches and required checks in `.aidevhelpers/repo-rules.json`. A no is remembered per user.
- `elev rules override <repo> <id>` allows one git write that a rule refused, for 10 minutes.
- `elev terminals scan` and `elev terminals setup [--all]` list the terminal types on the machine and, per type, register it for the helper and add a Windows Terminal profile (text insert, `.bak` backup) after a yes.
- `elev tools` (elevated, one UAC prompt) writes `install-tool.ps1` and its allowlist into the install folder, registers it and asks to approve it; `elev install-tool <name>` then runs it through the broker for `pwsh`, `gitbash`, `python` or `node` (winget). The MCP `tool_install` tool uses it after the user's yes.

## Running a script

```
elev run C:\path\to\repo scripts/debug/attach.ps1 -Port 5000
```

`run` does the missing prerequisites itself, each at most once:

- It registers the repo (administrator step).
- It starts the broker (one UAC prompt).
- If the script is not in the repo's `.elevated.json` and allow-all is off, it asks "add it to your allowed scripts?". On yes it records a
  user allow (see User allows). The repo is not changed.
- A script listed in `.elevated.json` but not yet approved is approved, with the console showing what is approved.

The first run of a session starts the broker for you: `elev` checks the heartbeat, and if the broker is silent it starts the installed
broker hidden with one UAC prompt, waits for it to answer, and reuses it afterwards. A declined prompt is not repeated for a minute, and a
broker outside the install folder is refused, never started over. `elev` then waits up to 10 minutes for the answer.

## Running a script in a visible window

`elev run C:\path\to\repo scripts/debug/attach.ps1 --visible` asks the broker for the same approved script, but in a normal pwsh window on your desktop. The broker runs it through a fixed wrapper under a transcript, with the arguments read from a JSON file, and keeps the window open after a failure so the error can be read; press Enter to close. The transcript comes back as the output. All the usual checks (owner, allowlist, fingerprint, approval) run first, and the run stops after 9 minutes. The MCP `script_run` tool uses it for `interactive: true, elevated: true`.

## Running the broker

The broker must run from `%ProgramData%\AiDevHelpers\script-helper\bin`, the admin-only folder
that setup creates. Copy `scripthelper-broker.exe` and the files beside it there, then start it
once, elevated and hidden:

```
Start-Process -FilePath "$env:ProgramData\AiDevHelpers\script-helper\bin\scripthelper-broker.exe" -Verb RunAs -WindowStyle Hidden
```

`elev` refuses a broker that runs from anywhere else, or from a folder that ordinary users can
write to.

It stops after 30 minutes with no requests, or when you ask it to:

```
New-Item -ItemType File "$env:ProgramData\AiDevHelpers\script-helper\broker.stop"
```

Stops are recorded in `script-helper\logs\audit.log`.

## User allows

A user allow lets one script run for this user without an entry in the repo's `.elevated.json`.
It is a per-user signed allow file in the admin-only data root. Only `elev` writes it, and only
after a yes from the user: from `elev run` (the prompt above) or from `elev allow-script`. Both need
administrator rights.

The entry pins the script's fingerprint and its exact arguments. The broker consults it when the
manifest does not allow a script, and refuses the run if neither the manifest nor a user allow
matches. A changed script has a different fingerprint, so it needs allowing again. The repo and
its `.elevated.json` are not changed, and the entry covers only this user, this script and these
arguments.

## Threat model

Blocked:
- Running a script that is not listed, or with arguments the manifest does not allow.
- Running a script that changed after it was approved (what runs is the staged copy, checked
  against the approval).
- Writing or editing a user allow without administrator rights; a user allow stops working when its script changes.
- A requester other than the project's owner.
- A requester writing into another user's queue, or reading another user's answers.
- Overwriting an approval, a registry entry, or an audit line.
- Hanging the broker: scripts are killed with their child processes at a time limit, and output
  is capped.

Not blocked:
- A process already running as an administrator can change anything here.
- A process running as your own user can start the broker itself if it can get a UAC approval.
- With `--allow-no-tpm` (no TPM 2.0), approvals are protected by directory ACLs alone, with no
  signature. That is weaker, and the setup says so.

## Self test

`tests/e2e/Run-EndToEnd.ps1` builds the two executables, runs the setup commands, starts the broker hidden,
runs `elev run` on an approved script and an unlisted one, stops the broker, and checks the audit log.
It points `ProgramData` at a temp folder, so real Script Helper data is untouched. Run it from an
elevated PowerShell 7 shell.

## Not built yet

- The second, non-admin account check is done. A standard account (`dragonfire\test`) is denied
  read access to the admin's queue folder, write access to it, and read access to the admin's
  results folder.

McpBridge exposes the same path as the `runElevatedScript` tool (`src/MCP/Core`). It runs `elev.exe`
from the server's folder, so copy `elev.exe` there. The tool does not approve anything: a person
still approves each script with `elev approve`.
