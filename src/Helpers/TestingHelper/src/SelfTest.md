# TestingHelper self test

Tools: `TestWatch`, `TestUnwatch`, `TestFailures`, `TestCoverage`, `TestAffected`, `LiveTestStart`, `LiveTestStop`.

## Full self test

Fixture: a scratch solution with one passing and one failing xUnit test.

- `LiveTestStart` (solutionPath = fixture): PASS when `ok` is true and `dotnet watch test` is running.
- `TestFailures`: PASS when the failing test appears within the watch timeout.
- `TestWatch` (solutionPath = fixture, ideHotReload = off): PASS when `ok` is true and `watching` is set.
- `TestUnwatch` (solutionPath = fixture): PASS when `ok` is true.
- `TestCoverage` (solutionPath = fixture, provider = Coverlet): PASS when `ok` is true and a coverage summary is returned.
- `TestAffected` (root = fixture, changedFiles = one file in the library project): PASS when `ok` is true, and `projects` holds the library's test project and the test projects of projects that reference it, but not an unrelated test project.
- `TestAffected` (root = fixture, no changedFiles): PASS when `ok` is true and `changed` matches the files git reports as changed.
- `LiveTestStop` (solutionPath = fixture): PASS when `ok` is true and the watch process is gone.

Record each as PASS, FAIL or NOT RUN with the exact arguments and reply.
