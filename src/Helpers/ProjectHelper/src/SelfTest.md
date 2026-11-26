# ProjectHelper self test

Tools: `ProjectTypesList`, `ProjectAudit`, `ProjectFixPlan`, `ProjectFixConfirm`, `ProjectAddPlan`, `ProjectAddConfirm`, `ProjectAddTestPlan`, `ProjectAddTestConfirm`, `WorkloadList`, `WorkloadCheck`, `WorkloadInstallPlan`, `WorkloadInstallConfirm`.

## Full self test

- `ProjectTypesList` (no arguments): PASS when `ok` is true and `count` is greater than zero.
- `ProjectTypesList` (`language` = `F#`): PASS when every item lists `F#` in `languages`.
- `ProjectAudit` (root = this repo): PASS when `ok` is true.
- `ProjectAudit` (root = a folder with no standards file): PASS when `ok` is false and a finding names the missing file.
- Unit tests: `dotnet test src/Helpers/ProjectHelper/tests/AiDevHelpers.Helpers.ProjectHelper.Tests.csproj`. PASS when every test passes.

Record each as PASS or FAIL with the exact arguments and reply.
