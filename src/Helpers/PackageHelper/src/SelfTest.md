# PackageHelper self test

Tools: `PackageOutdated`, `PackageRestore`. The `PackageCheck` tool is served from `GateTools` (see the GateHelper self-test).

## Full self test

Fixture: a scratch folder with a `.csproj` for a test project that lacks `xunit.analyzers`, a `requirements.txt` with one unpinned line, and a `package.json` with no lockfile.

- `PackageCheck` (root = fixture): PASS when findings name the missing `xunit.analyzers`, the unpinned requirement, and the missing lockfile.
- Unit tests: `dotnet test src/Helpers/PackageHelper/tests/AiDevHelpers.Helpers.PackageHelper.Tests.csproj`. PASS when every test passes.

Record each as PASS or FAIL with the exact arguments and reply.

## PackageRestore

This runs `dotnet restore`, so it needs network access to NuGet unless the packages are cached.

- `PackageRestore` (root = this repo): PASS when `ok` is true and `exit_code` is 0.
- `PackageRestore` (root = a folder that does not exist): PASS when `ok` is false and `error` names the folder.
- `PackageRestore` (root = a folder with no solution): PASS when `ok` is false and `error` says no solution was found.

## PackageOutdated

This runs `dotnet list package --outdated`, so the folder must be restored and have network access to NuGet.

- `PackageOutdated` (root = this repo): PASS when `ok` is true and every update has a `chosen` of null or a version whose major is at most the .NET major of its project for `System.*` and `Microsoft.Extensions.*` packages.
- `PackageOutdated` (root = a folder that does not exist): PASS when `ok` is false and `error` names the folder.
- `PackageOutdated` (root = a folder with no solution): PASS when `ok` is false and `error` says no solution was found.
