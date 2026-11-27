# ContainerHelper self test

Tools: `container` with actions `status`, `logs`, `start`, `stop`, `restart`, `exec`.

## Full self test

Fixture: a running container you may start and stop (for example `docker run -d --name tokensaver-selftest alpine sleep 600`). Docker or Podman must be on PATH and its daemon running.

- `container` action `status`, containerName `tokensaver-selftest`: PASS when `ok` is true, `runtime` is `docker` or `podman`, and `state` and `health` are strings (`health` is `none` without a healthcheck).
- `container` action `logs`, containerName `tokensaver-selftest`, tailLines 50: PASS when `ok` is true and `lines` is a number. `capped` is false for a small log.
- `container` action `logs`, tailLines 0: PASS when `ok` is false and `error` names `tailLines`.
- `container` action `stop`, containerName `tokensaver-selftest`: PASS when `ok` is true and the returned `status.state` is `exited`.
- `container` action `start`, containerName `tokensaver-selftest`: PASS when `ok` is true and the returned `status.state` is `running`.
- `container` action `restart`, containerName `tokensaver-selftest`: PASS when `ok` is true and the returned `status.state` is `running`.
- `container` action `exec`, containerName `tokensaver-selftest`, command `echo selftest`: PASS when `ok` is true, `exitCode` is 0, `stdout` contains `selftest`, and `timedOut` is false.
- `container` action `exec`, command `sleep 5`, timeoutSeconds 1: PASS when `timedOut` is true and `exitCode` is -1.
- `container` action `status`, containerName `-rf`: PASS when `ok` is false and `error` says the name is invalid. No runtime command runs.

Record each as PASS, FAIL or NOT RUN with the exact arguments and reply.
