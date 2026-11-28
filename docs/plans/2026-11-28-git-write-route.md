# Plan: git write route gaps (git_plan / git_confirm)

Date: 2026-11-28. Status: draft, reviewed (Sonnet, read-only), not approved.

## Goal

Close the gaps in the two-step git write route (`git_plan` then `git_confirm`) so the actions that were run as raw git become tool actions, and a token cannot run against a repo whose HEAD or branch moved after planning. Sources: dogfood rows 39, 46, 48, 49 (global log, `~/.claude/docs/dogfood.md`) and the 2026-11-26 `git_plan` timeout and branch-switch rows.

## Current state (from code)

- `SourceControlTools.GitPlan` (`src/MCP/Core/src/SourceControlTools.cs`) takes `action` or `sequence`, plus `paths`, `message`, `branch`. Write actions: stage, commit, pull, push, create_branch, delete_branch, switch, stash_push, stash_pop, stash_apply. It catches only `ArgumentException`.
- `SourceControlService` (`src/Helpers/SourceControlHelper/src/SourceControlService.cs`) builds argv in `BuildArguments`. Tokens are in-memory dictionaries. Nothing records HEAD at plan time.
- `push` with a branch runs `push origin <branch>`, which pushes the local branch of that name. No way to name a different destination. `push` with no branch pushes the current branch.
- No `merge`, no worktree action. `delete_branch` only runs `branch -d`.
- Timeouts: `CommandRequest.Timeout` already defaults to 10 minutes and kills the process tree (`CommandRunner.cs:19`). `ProcessGitRunner` already maps `TimedOut` to `ok: false` with `GitTimedOut` (`SourceControlService.cs:404-407`). So the "no timeout" premise is wrong. The 2026-11-26 hang had a 10-minute ceiling and its cause is not established. A likely cause is an interactive prompt (editor, credentials) with no tty.
- VS Code route (`VsCodeRoute`) maps stage, commit, switch, delete_branch (non-force), stash_*. push, pull and create_branch stay on git.exe.
- Existing tests assert exact git calls: `Assert.Empty(git.Calls)` on the VS Code stage route (`GitConfirmVsCodeRouteTests.cs:26, 58, 63`), `Assert.Single(git.Calls)` on push (`:41`), and no `rev-parse` in `RuleEnforcementTests.cs:160`.

## Decisions

1. New actions (merge, worktree_add, worktree_remove) run through git.exe only. Not routed to VS Code, so the extension is not changed or reinstalled.
2. Guard is HEAD **and** branch name, recorded at plan time for HEAD-sensitive actions only: commit, push, pull, merge, delete_branch. Stage and stash skip it, so the existing VS Code route tests keep their exact call counts.
3. Only step 1 of a sequence can be checked against plan time. Later steps cannot be known at plan time, and re-reading HEAD after each step checks nothing. So a sequence is checked once, before step 1, against `HeadAtPlan`. Document this.
4. Plan-time capture with no repo or an unborn HEAD gives `null` ("unknown"). `null` is compared as a value. Catch `InvalidOperationException` at the tool layer as well as `ArgumentException`.
5. Timeouts are not a flat 60 s (a flat limit breaks pull, push with hooks, and commit with hooks). Built in Unit 1: the existing 10-minute ceiling stays, plus a 5-minute no-output timeout for the silent-hang case, and `GIT_TERMINAL_PROMPT=0` so git never waits for a credential prompt. Per-action ceilings are not built: the ceiling already covers the long actions, so they were dropped as not needed.
6. Non-interactive argv everywhere: `merge --no-ff --no-edit <from>`.
7. Rules: `WriteActions` must include the new actions, because `CheckRulesAsync` is gated on it. `RuleGuard.Check` gets a merge case and the target is the current branch. Merge into a protected current branch is refused. Null current branch (detached HEAD, unreadable): refuse merge and commit, and say so in the tool text. Do not fail open.
8. `delete_branch` force, and the VS Code route: a force delete returns null from `VsCodeRoute` and stays on git.exe, because the route would otherwise run non-force `-d`. Covered by a test.

## Units

### Unit 1: HEAD and branch guard, per-action timeouts — DONE (red then green)

Status: red run 4 of 7 `HeadGuardTests` failing; green: SourceControlHelper 52/52, MCP Core 454/454. Route tests count writes only (`rev-parse` reads excluded in `FakeGit`). Guard reads run through git.exe, including on the VS Code route. Uncommitted-state note: the `.tokensaver/backups` folder appears in the repo and must not be committed.

- Add `HeadAtPlan` and `BranchAtPlan` to `PendingWrite`, and `HeadAtPlan` to `PendingSequence`. Capture them in `PlanAsync` and `PlanSequenceAsync` only for the HEAD-sensitive actions (decision 2).
- In `ConfirmAsync` and `RunSequenceAsync`, before step 1 only (decision 3), compare current HEAD and branch with the planned values. Refuse with new string `HeadMovedBeforeConfirm` when either differs.
- Per-action timeouts and `GIT_TERMINAL_PROMPT=0` (decision 5).
- Tests (fake runner, no real git for the guard): refuse after HEAD moves; refuse after a branch switch to the same commit; sequence commit-then-push passes; unborn HEAD gives null and compares as a value; timeout returns `ok: false`. Existing exact-call tests stay unchanged; the VS Code route makes no extra git call.

Done when: tests pass and a live scratch-repo check shows a branch switch between plan and confirm is refused.

### Unit 2: push to another ref — DONE

Status: tests in `PushRefTests.cs` (10 cases; the first red run was a compile failure because `pushRef` did not exist yet). Green: SourceControlHelper 62/62, MCP Core 454/454. `pushRef` is passed to push steps only in a sequence; a non-push action with pushRef is refused.

- `pushRef` parameter on `git_plan`, after `sequence` and before `ct`, optional. Accept `dst` or `src:dst`. Build `push origin <src>:refs/heads/<dst>`. Reject empty names and names starting with `-`. Refuse a call that gives both `branch` and `pushRef`.
- Set `PendingWrite.Branch` to the destination, so `RefusalAsync` at confirm checks the right branch.
- Tests: argv for both forms; rule refusal for a protected destination; invalid names refused at plan time.

Done when: `push origin HEAD:wip` is a plan, not a raw command (dogfood row 46).

### Unit 3: merge — DONE

Status: `MergeTests.cs` (9 cases; 6 red before the change, 3 passed because the old code refused unknown actions). Green: SourceControlHelper 71/71, MCP Core 454/454. Deviations: the source ref uses the existing `branch` parameter (no new `from` parameter); merge is HEAD-sensitive; merge into an unknown branch (detached or unborn) is refused. Commit's null-branch behaviour is unchanged, and the plan's "refuse commit on null" is not done. A real-git conflict and abort run is not done yet.

Original text:

- Action `merge` with optional `from` parameter. Argv `merge --no-ff --no-edit <from>`.
- Action `merge_abort`: precondition `MERGE_HEAD` exists, else refuse. Not allowed inside a sequence.
- After a conflict, list paths with a follow-up call `diff --name-only --diff-filter=U`, since `CONFLICT` lines alone are not enough.
- Tests: argv; empty `from` refused; abort refused without `MERGE_HEAD`; conflict list; scratch-repo test for a real conflict and `merge_abort`.
- Note the existing `[switch, commit]` plan-time rule check runs against the pre-switch branch (`SourceControlService.cs:166, 187`). Fix it in this unit, or log it separately.

Done when: merging `origin/wip` into a feature branch needs no raw git (dogfood row 46).

### Unit 4: worktree add, remove, and branch force-delete — DONE

Status: `WorktreeTests.cs` (10 cases, compile-red first). Green: SourceControlHelper 81/81, MCP Core 454/454. Deviations: new optional `path` (the worktree folder) and `mergedInto` (the force-delete ref) parameters, since `paths` and `branch` could not carry them cleanly. `worktree_add` always creates a new branch (`-b`); no base or existing-branch option. `worktree_remove` has no force: a dirty worktree is refused by git, and a locked one is refused. The current-worktree check compares the planning folder with the removed path, not `rev-parse --show-toplevel`. Sequences do not carry `path` or `mergedInto`. Force delete stays on git.exe in the VS Code route.

Original text:

### Unit 4 (original)

- `worktree_add` (`path`, `branch`, `base`, `newBranch` default true). `newBranch` true uses `-b`, so `create_branch` name rules apply. False checks out an existing branch.
- `worktree_remove` (`path`, `force` default false). Resolve `path` relative to `repoPath`. Refuse paths starting with `-`. Normalise slashes and case against `git worktree list --porcelain`. Refuse the main worktree and the current one. Locked worktrees need `force` twice. Git already refuses dirty trees, so test that rather than writing a separate check.
- `delete_branch` `force` with `mergedInto`: ancestor check (`merge-base --is-ancestor`) at plan **and** at confirm, since the branch can move. Refuse the checked-out branch and a branch checked out in another worktree. Force delete stays on git.exe (decision 8).
- Tests: argv; unlisted path refused; main or current worktree refused; locked needs double force; force delete refused when not an ancestor; a worktree-dir `repoPath` (where `.git` is a file) works.

Done when: the verification worktree and its merged branch can be removed through the tool (dogfood rows 39, 48).

### Unit 5: compatibility, strings, docs, dogfood log — DONE

Status: `resx_check` 0 (one unused key found and fixed), `localization_sync_check` 0, `lint_check` 0, `string_scan` 0 on the changed files. Docs updated: `mcp-tools.md`, `SelfTest.md`, `CHANGELOG.md`. Dogfood log rows updated in `~/.claude/docs/dogfood.md`. Tests: all 13 affected projects from `test_affected` run with `dotnet test`. Not run: the IDE steps of the lite gate (no Visual Studio instance on this worktree), `project_audit`, and the full gate.

Original text:

### Unit 5 (original)

- New `GitPlan` parameters are optional and come after `sequence`, so existing named and positional callers still work.
- Add new `Strings.resx` keys (`HeadMovedBeforeConfirm` and others). Run `localization_sync_check` and `resx_check`.
- `WriteActions` appears in the `UnknownWriteAction` message and the tool description: update both.
- Update `src/Common/src/Resources/mcp-tools.md`, the SourceControlHelper `SelfTest.md`, and `CHANGELOG.md`.
- Mark dogfood rows 39, 46, 48 and the timeout row as fixed in `~/.claude/docs/dogfood.md`, with the PR.

Done when: `SelfTestCoverageTests` passes, localization checks pass, and the docs name every new action.

## Verification

- Lite gate per the repo's rules, then `dotnet test` on `MCP.Core` and `SourceControlHelper` tests.
- Live check in a scratch repo outside the repo: each new action through `git_plan` and `git_confirm`, one HEAD-moved refusal, one branch-switch refusal, one merge conflict with abort.

## Open items

- The plan file is in the `feature/roslyn-helper-plan-units` checkout, untracked. Move it to its own branch before committing.
- The base branch for the work is not checked yet. Read the repo's CLAUDE.md first.
- The root cause of the 2026-11-26 hang is not established. Unit 1 adds per-action and no-output timeouts plus non-interactive git. Record what the live check finds in the log.
