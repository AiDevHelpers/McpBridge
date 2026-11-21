# ErrorHelper self test

Tools: `ErrorIngest`, `ErrorList`, `ErrorSnapshot`, `ErrorChanges`.

## Full self test

Fixture: build output text with one error (`CS1002`) and one warning (`CA1822`).

- `ErrorIngest` (output = the fixture text, solutionPath = fixture): PASS when `ok` is true.
- `ErrorList` (kind = errors): PASS when `ok` is true and `items` contains `CS1002`.
- `ErrorList` (kind = warnings, codes = CA1822): PASS when `count` is 1.
- `ErrorList` (kind = unknown): PASS when `ok` is false.
- `ErrorSnapshot`: PASS when `ok` is true and `snapshotId` is returned.
- `ErrorIngest` (output = the same text without the warning), then `ErrorChanges` (snapshotId from ErrorSnapshot): PASS when the reply shows the warning as removed.

Record each as PASS or FAIL with the exact arguments and reply.
