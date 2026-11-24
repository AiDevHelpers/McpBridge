# One helper exe for Script Helper

Status: in progress. Decisions from the user: one exe replaces `elev` and `scripthelper-setup` (the broker stays its own exe, because it must run from the admin-only folder); arguments use `System.CommandLine` (2.0.14, already in the local NuGet cache); steps that need administrator rights relaunch the same exe elevated once (one UAC prompt) and return; a script that is not allowlisted, with allow-all off, is offered to the user as an allowed script, stored in a per-user allow file in the admin-only data root (not the repo).

## Shape

- The `client` project becomes the single exe and keeps the name `elev` (the MCP server, docs and install folder already use it). It absorbs the setup program (signer, TPM mode, admin-only folders) and its commands; the `setup` project is removed.
- Commands: `run <repo> <script> [args]` (default form `elev <repo> <script>` stays), `run --visible`, `verify`, `scan`, `repo-scan`, `repo-setup`, `rules override`, `terminals scan|setup`, `install-tool`, `setup tools`, `register`, `approve`, `allow-all`, `allow-script`.
- `run` does the prerequisites itself, in order, each only when missing: register the project (elevated step), start the broker (existing one UAC prompt), and when the script is not allowed (not in `.elevated.json`, not in the user allows, allow-all off) ask in the console whether to add it to the user allows, then approve its fingerprint (elevated step, existing confirmation). Then it sends the request.
- Elevated steps: `elev` starts itself with `runas` and the hidden verb `elevated-step <command...>`; the elevated copy refuses anything that is not one of the setup commands and asks its confirmations in its own console.

## User allows

A signed document in the admin-only data root, per project: `ProjectId`, `ProjectRoot`, `Version` (replay, its own counter), and entries `{path, args}` with the same glob rules as `.elevated.json`. Written only by an elevated step after a console yes. The broker consults it when the repo manifest does not allow a script; the per-script approval (fingerprint) still applies. Editing the repo cannot add to it.

## Units

1. `UserAllowStore` (sign, save, check) and the broker rule, with tests.
2. `setup allow-script <repo> <script>`: elevated, asks, writes the user allow entry, then approves the script.
3. `RunPlanner`: decides the missing prerequisites from the facts (registered, broker answering, allowed by manifest, allowed by user allows, allow-all, approved), with tests.
4. The single exe: System.CommandLine commands over the existing Core code, self-elevation, absorb setup, remove the `setup` project.
5. Docs, e2e script, dogfood row 21 (simplify allow-all) updated.

## Progress
- Units 1 and 2 done. `UserAllowStore` (signed per-project document in `<data>\userallow`, own replay counter `<id>-ua`, entries pin path, exact arguments and fingerprint, so an entry is the approval) and the broker rule: when the repo manifest does not allow a script (or has no `.elevated.json`), a verified user allow for that script, arguments and fingerprint does; a changed script, other arguments, a copied or replayed file or a bad signature are denied; a manifest script still needs its own approval. `SetupCommands.AllowScript` (elevated, console confirmation, the repo is not changed, refuses to overwrite an untrusted file). ScriptHelper 417 tests.
