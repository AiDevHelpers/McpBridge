# WslHelper self test

Tools: `wsl` with actions `status`, `health`, `run`, `debug_setup`.

## Full self test

Fixture: WSL 2 with at least one registered distro (for example `wsl --install -d Ubuntu`). Use distro name `Ubuntu` below; substitute your own. `wsl.exe` must be on PATH.

- `wsl` action `status`: PASS when `ok` is true, `available` is true, `distroCount` is at least 1, and each entry in `distros` has `name`, `isDefault`, `state` (`Running` or `Stopped`) and `version`.
- `wsl` action `health`, distro `Ubuntu`: PASS when `ok` is true, `healthy` is true, and `kernel` and `user` are non-empty strings.
- `wsl` action `run`, distro `Ubuntu`, command `echo selftest`: PASS when `ok` is true, `exitCode` is 0, `stdout` is `selftest`, `timedOut` is false and `capped` is false.
- `wsl` action `run`, command `sleep 5`, timeoutSeconds 1: PASS when `timedOut` is true and `exitCode` is -1.
- `wsl` action `run`, command `echo x`, timeoutSeconds 0: PASS when `ok` is false and `error` names `timeoutSeconds`. No command runs.
- `wsl` action `run`, distro `-rf`, command `echo x`: PASS when `ok` is false and `error` says the distro name is invalid. No command runs.
- `wsl` action `debug_setup`, runtime `ruby`, programPath `/app/a.rb`: PASS when `ok` is false and `error` names the runtime. No command runs.
- `wsl` action `debug_setup`, runtime `node`, distro `Ubuntu`, programPath `/tmp/selftest/index.js` (no write): PASS when `ok` is true, `launchConfigName` is `WSL: Node Launch`, `launchConfig.useWSL` is true and `configWritten` is false.
- `wsl` action `debug_setup`, runtime `node`, distro `Ubuntu`, write true, workspaceRoot a throwaway folder with no `.vscode`: PASS when `configWritten` is true and `<workspaceRoot>\.vscode\launch.json` exists. Run it a second time: PASS when `configWritten` is false and the file is unchanged.

Record each as PASS, FAIL or NOT RUN with the exact arguments and reply.
