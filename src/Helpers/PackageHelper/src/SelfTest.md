# PackageHelper self test

No tool of its own. The `PackageCheck` tool is served from `GateTools` (see the GateHelper self-test).

## Full self test

Fixture: a scratch folder with a `.csproj` for a test project that lacks `xunit.analyzers`, a `requirements.txt` with one unpinned line, and a `package.json` with no lockfile.

- `PackageCheck` (root = fixture): PASS when findings name the missing `xunit.analyzers`, the unpinned requirement, and the missing lockfile.
- Unit tests: `dotnet test src/Helpers/PackageHelper/tests/AiDevHelpers.Helpers.PackageHelper.Tests.csproj`. PASS when every test passes.

Record each as PASS or FAIL with the exact arguments and reply.
