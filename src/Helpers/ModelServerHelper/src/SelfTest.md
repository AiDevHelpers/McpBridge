# ModelServerHelper self test

Tools: `ModelServerStatus`, `ModelServerCapacity`, `ModelGenerate`, `ModelChat`.

## Full self test

Fixture: none required. The SimpleModelServer router may or may not be running.

- `ModelServerStatus`: PASS when `ok` is true and the reply has a `url` and a boolean `running`. When `running` is true, `models` is a list. When it is false, `ok` is still true.
- `ModelServerCapacity`: PASS when `ok` is true and `maxConcurrentSummaryRequests` is at least 1. `fromFallback` is true when the router is not running.
- `ModelServerCapacity` (refresh = true): PASS when `ok` is true.

- `ModelGenerate` (model: the first model from `ModelServerStatus`, prompt: "Reply with the word ok", timeoutSeconds: 60): PASS when `ok` is true and `response` is a non-empty string. NOT RUN when the router is not running or lists no model; say which.
- `ModelChat` (same model, messages: one user turn with the same prompt): PASS when `ok` is true and `content` is a non-empty string. NOT RUN under the same condition.

Record each as PASS, FAIL or NOT RUN with the exact arguments and reply.
