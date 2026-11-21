# Follow-on work, 2026-11-21

Status: revised after the Sonnet review. Awaiting approval. Builds on the archived [helpers follow-up plan](archived/2026-11-20-helpers-followup.md), which is done.

## Decisions

- D1 Source control helper uses the IDE source control APIs. Feasibility is spiked first (Phase 2), because Visual Studio may not offer pull, push and stash. A fallback to the git CLI is decided after the spike.
- D2 Output helper writes to the Visual Studio Output window and the VS Code Output panel, chosen per call. The per-repo choice is stored in the shared repo config (D5).
- D3 Package helper checks NuGet (`nuget.config`, `Directory.Packages.props`, xunit analyzers, Microsoft.CodeAnalysis.NetAnalyzers), pip (`requirements.txt` or `pyproject.toml`) and npm (`package.json` and its lockfile).
- D4 Lint helper aligns `.editorconfig`, `dotnet format` and analyzer severities, and checks ESLint for TypeScript and JavaScript. TSLint is reported as deprecated, with ESLint recommended. Detection: `tslint.json` or a `tslint` dependency.
- D5 One per-repo config file, `.claude/aidevhelpers.json`, holds the output channel locations and the preferred IDE. Both items read and write it.
- D6 The full gate is the `full-gate` skill: the repo's `.claude/reference/full-gate.md` if it exists, otherwise stack detection (dotnet, npm, pip, go, cargo). The gate helper runs the same steps. The package helper's gate step and the gate helper are one thing, owned by Phase 6.
- D7 Write actions (commit, push, stage, branch, stash) need the two-step approval used for extension Add (D13). A commit never adds the Claude attribution lines.

## Answered open questions

- Q1 Gate definitions: answered from the `full-gate` skill (D6). The lite gate definition was not found in the skills; check `.claude/reference/` before Phase 6.

## Open questions (block the named phase)

- Q2 Preferred IDE: does a stored preference override the ambiguity error (ide-bridge.md routing), or only break a tie? Blocks Phase 3.
- Q3 Localization mechanism: `.resx` for the .NET helpers and a JSON table for the TypeScript side, or one approach? Which languages? Blocks Phase 7.
- Q4 Self-test docs: is each helper's self-test markdown embedded as a resource in its library, and how does the bridge read it? What counts as a "full" self test? Blocks Phase 5.

## Phases (each ends with a review gate)

### Phase 1: Lint helper (D4, D7 not needed)
- Acceptance: a repo with mismatched `.editorconfig`, `dotnet format` and ESLint settings reports each mismatch, and fixing them makes the check pass. TSLint is reported as deprecated.
- Gate: the helper's unit tests, plus the lint check on this repo.

### Phase 2: Source control spike, then helper (D1, D7)
- Spike: confirm which of status, diff, stage, commit, pull, push, branch, switch and stash each IDE's API supports. Record the result in the plan.
- Acceptance (after the spike): each supported operation returns a structured result, and each write needs two-step approval.
- Gate: tests against a scratch repo, and a live check in each IDE.

### Phase 3: Preferred IDE and the repo config (D5, Q2)
- Acceptance: a stored preference answers tool calls without asking, and a call with an explicit `ide` still wins.
- Gate: tests for the config read and write, and for the precedence rule.

### Phase 4: Output helper (D2, D5)
- New bridge actions for creating and opening a named channel. Needs fixtures and protocol doc entries (Phase 8).
- VS Code cannot read other channels (ide-bridge.md Limits). Creating and writing is supported, reading back is not.
- Acceptance: the log helper writes to a channel, and the channel location persists per repo.
- Gate: live check in both IDEs.

### Phase 5: Self tests (Q4)
- Acceptance: each helper has an embedded self-test markdown, and the bridge reports its version from the Core DLL. Criteria for "full" come from Q4.
- Gate: a test that each helper's markdown is present.

### Phase 6: Package helper and gate helper (D3, D6)
- Package: check the configs and packages in D3, then run the full gate's build step.
- Gate: run the full gate (D6) on this repo. Lite gate once its definition is found.
- Extend the test helper to run only the affected tests.
- Gate: unit tests, plus a full-gate run on this repo.

### Phase 7: Localization (Q3)
- Last, because it touches every helper's strings and would conflict with the phases above.
- Acceptance: no user-facing string is hard-coded in a helper. The languages are those from Q3.
- Gate: a test that fails on a hard-coded string, plus the existing suites.

### Phase 8: Docs, solution and fixtures
- Update `AiDevHelpers.slnx`, `conformance/protocol.md`, `docs/ide-bridge.md` and the README for each new tool and action.
- Add conformance fixtures for each new bridge action.
- Give each new helper's library a NuGet package (per-helper versioning, as in D19) and add it to the CI pack list.
- Gate: the tool count matches the new total, and the protocol doc and fixtures match the code.

## Carried-over items

Fix:
- The Insiders `test.hotReload` is blank (owner: the user sets the option; the reply is `null` until then).
- The flaky `StopClosesThePipe_AndStartReopensIt` test: fix the test to wait for the pipe to close instead of sleeping.
- The silent-instance timeout test runs only locally. Add it to the Bridge test project's CI run.

Accepted (not work):
- D15: coverage lives in `TestingTools`.
- D28: Visual Studio extension listing is not built.
- D30: Visual Studio coverage uses the `dotnet-coverage` CLI.
- Bookmarks: only open documents are visible, and names aren't stored.
- Only `main` and `dev` require checks. `wip` is the working branch.

Verify on a real install:
- The `.vsconfig` key `aidevhelpersErrors`.
- Whether Claude Code displays MCP log notifications.

Decide:
- The `2005` error for a multi-ID VSIX install (fix or accept; the dialog install works).
- The VS Code bridge stall after stacked-window restarts (investigate or accept).

## Verification gate

- Each phase's gate passes, and the stdio server lists the new tools.
- Each item that touches a running IDE is live-checked.
