# SourceControlHelper self test

Tools: `GitStatus`, `GitDiff`, `DiffSummarize`, `GitPlan`, `GitConfirm`, `PrChecks`, `CiFailure`.

## Full self test

Fixture: a scratch git repo with one commit on `main` and one modified tracked file.

- `GitStatus` (repoPath = fixture): PASS when `ok` is true, `branch` is `main`, and `changes` lists the modified file with `worktree` `M`.
- `GitDiff` (repoPath = fixture, staged = false): PASS when `ok` is true and `output` contains the added line.- `DiffSummarize` (repoPath = fixture, staged = false): PASS when `ok` is true, `files` lists the changed file, `added` is at least 1 and `summary` is null with a `note` (no model given).

- `GitPlan` (repoPath = fixture, action = stage, paths = the modified file): PASS when `ok` is true and `token` is returned. The file must still show unstaged in `GitStatus`.
- `GitConfirm` (token from the previous call): PASS when `ok` is true, and `GitStatus` now shows the file staged.
- `GitConfirm` (same token again): PASS when `ok` is false and `output` says the token is used or unknown.
- `GitPlan` (action = rebase_hard): PASS when `ok` is false and `error` names the unknown action.

Record each as PASS or FAIL with the exact arguments and reply. Use a scratch repo only.

## PrChecks and CiFailure

These read GitHub through `gh`, so they need `gh` signed in and a repo with a pull request.

- `PrChecks` (repoPath = a repo with an open pull request, pr = its number): PASS when `ok` is true and `checks` lists each job with `name`, `state` and `bucket`.
- `PrChecks` (pr = a number that has no pull request): PASS when `ok` is false and `error` is not empty.
- `CiFailure` (same repo, a pull request whose checks all pass): PASS when `ok` is false and `error` says no check has failed.
- `CiFailure` (a pull request with a failed check): PASS when `ok` is true, `job` and `runId` are set, and `lines` holds the error lines of the log. NOT RUN when no such pull request exists.
