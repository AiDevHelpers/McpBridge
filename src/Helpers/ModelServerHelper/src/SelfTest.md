# ModelServerHelper self test

Tools: `ModelServerStatus`, `ModelServerCapacity`.

## Full self test

Fixture: none required. The SimpleModelServer router may or may not be running.

- `ModelServerStatus`: PASS when `ok` is true and the reply has a `url` and a boolean `running`. When `running` is true, `models` is a list. When it is false, `ok` is still true.
- `ModelServerCapacity`: PASS when `ok` is true and `maxConcurrentSummaryRequests` is at least 1. `fromFallback` is true when the router is not running.
- `ModelServerCapacity` (refresh = true): PASS when `ok` is true.

Record each as PASS, FAIL or NOT RUN with the exact arguments and reply.
