# Script Helper

Runs allowlisted debug scripts elevated, without a UAC prompt per script. One elevated broker
is started with one UAC prompt. Unelevated callers send requests through a per-user queue, and
the broker runs only scripts that were approved for exactly the bytes it runs.

## Parts

| Executable | Runs as | Purpose |
|---|---|---|
| `scripthelper-setup.exe` | elevated | Register a project root, approve a script (`register`, `approve`). |
| `scripthelper-broker.exe` | elevated, started hidden | Answers requests from the queue, heartbeat, stop, audit. |
| `elev.exe` | unelevated | Sends one request and prints the script's output and exit code. |

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

Run from an elevated shell:

```
scripthelper-setup.exe register C:\path\to\repo
scripthelper-setup.exe approve C:\path\to\repo scripts/debug/attach.ps1
```

`approve` shows the script path, every file it pulls in, the fingerprint and the allowlist hash,
then waits for `y`. The approval is refused if the script or manifest changes later.

For a caller that asks the person itself (for example an AI that shows the prompt in chat):

```
scripthelper-setup.exe approve C:\path\to\repo scripts/debug/attach.ps1 --prepare
scripthelper-setup.exe approve C:\path\to\repo scripts/debug/attach.ps1 --confirm <fingerprint> <allowlistHash>
```

`--confirm` writes the approval only if the script and manifest still hash to the values the
person was shown.

## Running the broker

Start it once, elevated and hidden:

```
Start-Process -FilePath scripthelper-broker.exe -Verb RunAs -WindowStyle Hidden
```

It stops after 30 minutes with no requests, or when you ask it to:

```
New-Item -ItemType File "$env:ProgramData\AiDevHelpers\script-helper\broker.stop"
```

Stops are recorded in `script-helper\logs\audit.log`.

## Running a script

```
elev.exe C:\path\to\repo scripts/debug/attach.ps1 -Port 5000
```

`elev` checks the broker's heartbeat first and does not send anything if the broker is not
answering. It then waits up to 10 minutes for the answer.

## Threat model

Blocked:
- Running a script that is not listed, or with arguments the manifest does not allow.
- Running a script that changed after it was approved (what runs is the staged copy, checked
  against the approval).
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

## Not built yet

- The broker-binary install location check (the client should refuse a broker outside an
  admin-only folder).
- Verification that the broker window stays hidden, and an end-to-end run with a second,
  non-admin account. Both are manual checks.
- Integration into McpBridge.
