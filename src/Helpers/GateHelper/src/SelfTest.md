# GateHelper self test

Tools: `GatePlan`, `GateRun`, and `PackageCheck` (served from `GateTools`).

## Full self test

Fixture: a scratch folder with a `.claude/reference/full-gate.md` whose `gate` block has one passing command (`dotnet --version`) and one advisory command marked `!`.

- `GatePlan` (root = fixture): PASS when `ok` is true and `steps` lists both commands in order, with `allowFailure` true for the advisory one.
- `GateRun` (root = fixture): PASS when `ok` is true and each step has `passed` true.
- `GateRun` (root = a folder with a failing command in its gate block): PASS when `ok` is false, the failing step has `passed` false, and later steps have `skipped` true.
- `GatePlan` (root = a folder with no known stack and no reference file): PASS when `ok` is false and `error` says to ask for the gate commands.
- `PackageCheck` (root = fixture): PASS when `ok` is true and `findings` is a list.

Unit tests: `dotnet test src/Helpers/GateHelper/tests/AiDevHelpers.Helpers.GateHelper.Tests.csproj`. PASS when every test passes.

Record each as PASS, FAIL or NOT RUN with the exact arguments and reply.
