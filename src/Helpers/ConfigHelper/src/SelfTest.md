# ConfigHelper self test

Tools: `ConfigResolve`, `ConfigRead`.

## Full self test

Fixture: a scratch solution folder with a `Properties\launchSettings.json` and an `appsettings.json` that holds a `Password` value.

- `ConfigResolve` (kind = LaunchSettings, solutionPath = fixture): PASS when `ok` is true and the path points at the fixture's `launchSettings.json`.
- `ConfigResolve` (kind = Unknown): PASS when `ok` is false and `error` names the allowed kinds.
- `ConfigRead` (path = the fixture `appsettings.json`, solutionPath = fixture): PASS when `ok` is true and the `Password` value is redacted in `content`.
- `ConfigRead` (path = a file outside the allowed roots): PASS when `ok` is false.

Record each as PASS or FAIL with the exact arguments and reply.
