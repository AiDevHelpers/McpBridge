# container contract

Ported from TokenSaver `DevLoopTools` (`ContainerLogs`, `ContainerStart`, `ContainerStop`, `ContainerRestart`, `ContainerExec`, `DockerPreflightAsync`).
Differences by design: JSON results, Docker and Podman, no model distillation, `CommandRunner` for processes. Docker or Podman must be on PATH.

## Tool
`container(action, containerName?, tailLines?, errorLinesOnly?, command?, timeoutSeconds?, runtime?)`. `action` is trimmed and case-insensitive.
Actions: `status`, `logs`, `start`, `stop`, `restart`, `exec`. Unknown action: `ok=false`, error lists the valid actions.

## Runtime
- `runtime` argument: `docker` or `podman`. Omitted: `docker --version` first, else `podman --version`. Neither: error "No container runtime found; install Docker or Podman".
- Every result includes `runtime` (the one that ran). A `docker` command can be a Podman shim, so the detected name is reported, not assumed.
- Explicit `runtime` that is not installed: error naming it.

## Preflight (every action except a refused name)
1. Name check: `^[A-Za-z0-9][A-Za-z0-9_.-]*$`. Else error "Invalid container name ...". Rejects a leading dash and shell characters.
2. CLI check: `<runtime> --version`. Non-zero exit: "<runtime> CLI is installed but failed (exit N): ...".
3. Daemon check: `docker info --format {{.ServerVersion}}` or `podman info --format {{.Version.Version}}`. Failure messages:
   - Docker: "Docker is installed but the daemon is not running (start Docker Desktop): ...".
   - Podman: "Podman is installed but no machine is running (run: podman machine start): ...".
4. Inspect: `<runtime> inspect -f <format> -- <name>`. Format (Go template) yields `status|health|exitCode|finishedAt`. Health: `.State.Health.Status` when set, else `none`; a missing healthcheck on Podman prints `<nil>`, which maps to `none`. Failure: "Container '<name>' not found: ...".

## Status (`status`)
Result: `{ok, runtime, container, state, health, exitCode, finishedAt}`. `health` is `none` when the container has no healthcheck.

## Logs (`logs`)
- `tailLines` default 500, must be at least 1 (else error "tailLines must be 1 or greater."). Command: `<runtime> logs --tail N -- <name>`.
- Works on a stopped container.
- Reads stdout and stderr: a container's stderr is written to the client's stderr, so the combined text is used.
- `errorLinesOnly=true`: returns only lines matching `error|exception|fail|fatal|panic` (case-insensitive).
- Result: `{ok, runtime, container, lines, logs, capped}`. `logs` capped at 20,000 characters; `capped` says whether it was cut.

## Start, stop, restart
- Command: `<runtime> start|stop|restart -- <name>` (`--` before the name). Stop uses the runtime's default 10 s grace period.
- Then preflight again and return the new status. Non-zero exit: "<runtime> <verb> <name> failed (exit N): <stderr>".
- Result: `{ok, runtime, container, action, status...}`.

## Exec (`exec`)
- `command` required, not empty. `timeoutSeconds` 1-600, default 60; else error "timeoutSeconds must be between 1 and 600.".
- Argument list: `<runtime> exec <name> /bin/sh -c <command>`. The caller's text is one list element, never a shell string on the Windows side.
- Result: `{ok, runtime, container, exitCode, stdout, stderr, timedOut, capped}`. Each stream capped at 20,000 characters.
- On timeout: `ok=false`, `timedOut=true`, `exitCode=-1`, error "Timed out after N s; the docker client was killed (the command may still run inside the container).". The client is killed; the command may still run inside the container.

## Errors
Refusals and failures are `{ok:false, error}`. A launch failure of the runtime is reported with its message, not thrown.

## Not ported
- TokenSaver's `RawWriter` (full log file) and `DistillAsync` (model summary).
- `ManagedProcessRegistry` tracking: not reachable from helpers; not needed for these actions.
