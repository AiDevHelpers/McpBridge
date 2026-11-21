# SourceControlHelper self test

Tools: `GitStatus`, `GitDiff`, `GitPlan`, `GitConfirm`.

## Full self test

Fixture: a scratch git repo with one commit on `main` and one modified tracked file.

- `GitStatus` (repoPath = fixture): PASS when `ok` is true, `branch` is `main`, and `changes` lists the modified file with `worktree` `M`.
- `GitDiff` (repoPath = fixture, staged = false): PASS when `ok` is true and `output` contains the added line.
- `GitPlan` (repoPath = fixture, action = stage, paths = the modified file): PASS when `ok` is true and `token` is returned. The file must still show unstaged in `GitStatus`.
- `GitConfirm` (token from the previous call): PASS when `ok` is true, and `GitStatus` now shows the file staged.
- `GitConfirm` (same token again): PASS when `ok` is false and `output` says the token is used or unknown.
- `GitPlan` (action = rebase_hard): PASS when `ok` is false and `error` names the unknown action.

Record each as PASS or FAIL with the exact arguments and reply. Use a scratch repo only.
