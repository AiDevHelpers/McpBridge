# ProjectHelper self test

Tools: `ProjectTypesList`. The other project tools (`ProjectAudit`, `ProjectFixPlan`, `ProjectFixConfirm`, `ProjectAddPlan`, `ProjectAddConfirm`, `ProjectAddTestPlan`, `ProjectAddTestConfirm`) are listed here as their units land.

## Full self test

- `ProjectTypesList` (no arguments): PASS when `ok` is true and `count` is greater than zero.
- `ProjectTypesList` (`language` = `F#`): PASS when every item lists `F#` in `languages`.
- Unit tests: `dotnet test src/Helpers/ProjectHelper/tests/AiDevHelpers.Helpers.ProjectHelper.Tests.csproj`. PASS when every test passes.

Record each as PASS or FAIL with the exact arguments and reply.
