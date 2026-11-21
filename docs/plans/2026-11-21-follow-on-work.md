# Follow-on work and open items, 2026-11-21

Status: draft, awaiting approval. Builds on the archived [helpers follow-up plan](archived/2026-11-20-helpers-followup.md).

## Decisions

- D1 Source control helper uses the IDE source control APIs (VS and VS Code), not the git CLI.
- D2 Output helper writes to both the Visual Studio Output window and the VS Code Output panel. The target is chosen per call and remembered per repo in a JSON config.
- D3 Package helper checks NuGet (`nuget.config`, `Directory.Packages.props`, xunit analyzers and Microsoft.CodeAnalysis.NetAnalyzers), pip (`requirements.txt` or `pyproject.toml`), and npm (`package.json` and lockfiles). It also has a full gate build step.
- D4 Lint helper aligns `.editorconfig`, `dotnet format` and analyzer severities. It also checks ESLint for TypeScript and JavaScript. TSLint is deprecated, so the helper reports it and recommends ESLint.

## Open questions (block the named item)

- Q1 Gate definitions: read the full and lite gate definitions in the skills (`full-gate`, `lint`) before the gate helper is designed. Blocks the gate helper.
- Q2 Preferred IDE: the store and the rule for choosing an IDE when both are open. Blocks the preferences item.
- Q3 Localization: which languages, and which strings are in scope (UI, MCP tool descriptions, static returns). Blocks localization.
- Q4 Self-test docs: whether each helper's self-test markdown is embedded as a resource in the library, and how the bridge reads it. Blocks the self-test item.

## Open items carried over

- The `.vsconfig` key `aidevhelpersErrors`: unverified, left as is at the user's request.
- MCP log notifications: unverified whether Claude Code displays them.
- D28, Visual Studio extension listing: not built.
- D30, Visual Studio coverage through the bridge: not built. Coverage uses the `dotnet-coverage` CLI.
- D15, coverage placement: lives in `TestingTools`, not `IdeTestRunService` as planned.
- Bookmarks: only open documents are visible, and names aren't stored.
- The `2005` error for a multi-ID VSIX install. The dialog install works.
- The VS Code bridge stall after stacked-window restarts. Not root-caused.
- Flaky test `StopClosesThePipe_AndStartReopensIt`.
- The silent-instance timeout test runs only locally, not in the repo's CI.
- Insiders' `test.hotReload` is blank, so its reply is `null` until the option is set there.
- Only `main` and `dev` require checks. `wip` is the working branch.

## New work

1. Source control helper: status, diff, stage, commit, pull, push, branch, switch and stash through the IDE APIs. VS first, then VS Code.
2. Output helper: create or open a named channel in the VS Output window or the VS Code Output panel. Let the log helper write to a channel instead of a terminal. Remember the channel per repo in a JSON config, or reuse the existing config.
3. Preferred IDE: store the chosen IDE per repo so tools don't ask again. Depends on Q2.
4. Self tests: each helper ships a full self-test markdown, embedded in its library, and the bridge references it in the Core DLL version. Depends on Q4.
5. Localization: move all user-facing text and static returns in the helpers into resources. Depends on Q3.
6. Package helper: check NuGet, pip and npm configs and packages (xunit analyzers and Microsoft.CodeAnalysis.NetAnalyzers for .NET), and add a full-gate build step.
7. Gate helper: full and lite gate methods, from the definitions in Q1. Extend the test helper to run the affected tests.
8. Lint helper: align `.editorconfig`, `dotnet format` and analyzer severities, and check ESLint configs for the TypeScript and JavaScript projects (TSLint is reported as deprecated).

## Verification gate

- Each helper's tests pass, and the stdio server lists the new tools.
- Each new item is live-checked against a running IDE where it touches one.
