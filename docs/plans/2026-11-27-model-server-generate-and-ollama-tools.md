# Plan: model-server generate/chat and the Ollama-driven tools in aidevtools

Branch: `feat/modelserver-file-edit` (same branch, user choice). Worktree: `F:\aidevhelpers\wip-worktree`.
Follow-up to `docs/plans/2026-11-27-wsl-and-container-tools.md` (its "Follow-ups" section) and to `docs/plans/archived/2026-11-27-modelserver-and-file-edit.md` (U3 scope was status and capacity only).
Source of behavior: TokenSaver repo `F:\aidevhelpers\TokenSaver` (read only). Status: draft, awaiting review.

## Source files (TokenSaver, read only)
- Generate and chat: `TokenSaver.Core/TokenSaver.OllamaIntegration/Services/OllamaService.cs` (`CallModelAsync` overloads at :61-:100, `CallModelOnEndpointAsync`, `EmbedAsync` :123, `WaitForReadyAsync` :232).
- Endpoint registry: `TokenSaver.Core/TokenSaver.OllamaIntegration/Services/OllamaEndpointRegistry.cs` (373 lines; selection strategy, named endpoints).
- Tools: `LocalModelHelperTools.cs` (`CleanAndChat` :97, `ListOllamaEndpoints` :463), `TokenEconomyTools.cs` (`ModelRoute` :64, routing keys code, chat, summarize, embed, rerank),
  `DevLoopTools.cs` (`DiffSummarize`, already noted in the WSL plan's source list), `AutoWorkflowTools.cs` (`CodeWork` :52, start and continue),
  `LanguageHelperTools.cs` (`FixCode` :100, operations enable-analysis, fix-syntax, fix-imports, fix-namespace, format), `CodeHelperTools.cs` (`ExecuteStep`, from `ManagePlan`).

## Decisions
- Scope of the first unit set: generate/chat and endpoint listing, then `model_route`, `diff_summarize` and `clean_and_chat`. The Ollama-driven editing tools
  (`code_work`, `fix_code`, `execute_step`) come last, because they write files and need the `file_edit` engine and the path guard.
- Ollama only, over HTTP, same as `ModelServerHelper`: the router at the discovered address (`ModelServerAddress`, loopback only). Endpoint names come from the router config,
  not from TokenSaver's `AgentConfigService`.
- Output limits: generated text capped at 20,000 characters with a `capped` flag. Prompts are not logged. Timeout 120 s for generate, configurable 1-600.
- `diff_summarize` takes a git diff from `git` through `CommandRunner` (no hand-rolled process), uses the repo path guard, and returns the summary as JSON. It does not call a
  model until U3 exists.
- Wiring per helper, as in earlier plans (csproj, resx for every user-facing string, SelfTest.md, slnx, ci.yml pack step, `WithTools<>`, `SelfTestTools`,
  `SelfTestCoverageTests`, `mcp-tools.md`, `docs/ide-bridge.md`, README).
- Editing tools (U8-U10) must keep the workspace guard and backups from `FileEditHelper` (cross-helper reference: decide in U8 whether to move the guard into Common,
  as TokenSaver's `WorkspacePathGuard` lives in its Utilities; the wsl review flagged the same cross-helper question).

## Units
- [x] U1. (done: contract written and checked against OllamaService.cs and the router contract; every overload mapped or dropped; chat uses `/v1/chat/completions` because the router contract names it, `architecture.md` says `/api/chat`; endpoint pinning dropped because the router has no endpoint selection) Contract `docs/Host-MCP/model-generate-contract.md`: request and response for generate and chat, endpoint names, selection rules, timeout, capped output,
  error text, and what TokenSaver's `CallModelAsync` overloads map to. Done: every overload either mapped or listed as dropped.
- [ ] U2. `ModelServerHelper` generate and chat. Failing tests first with the existing fake `HttpMessageHandler` pattern: request body shape (`/api/generate`, `/api/chat`
  as the router documents them; check the router's contract doc in `F:\aidevhelpers\TokenSaver\TokenSaver.SimpleModelServer` before writing), non-2xx, timeout, cancellation,
  cap. Done: red then green.
- [ ] U3. Endpoint listing: `ModelServerClient.ListEndpointsAsync` (router endpoints and their status). Tests: parse, empty list, unreachable router.
- [ ] U4. `model_server` tools for U2-U3 (`model_generate`, `model_chat`, `model_endpoints`) in MCP.Core, tool tests (refusals and JSON shape), self-test entries, docs wiring.
  Note: `list_ollama_endpoints` is the TokenSaver name; aidevtools name is `model_endpoints`, and the retire list maps it.
- [ ] U5. `model_route` in `ModelServerHelper`: routing keys code, chat, summarize, embed, rerank mapped to a named endpoint or the default; unknown key refused.
  Tests for each key and unknown key. Tool in MCP.Core with tests.
- [ ] U6. `diff_summarize` in a new `DiffHelper` (or `SourceControlHelper`, decided in U6 by checking that project's existing scope). Diff through `CommandRunner`
  (`git diff`, `--staged` optional), path guard, returns the diff stats and the raw diff size; the summary step uses U2 generate when present, otherwise returns
  diff stats only with a note. Tests with a fake runner. Tool with tests.
- [ ] U7. `clean_and_chat` in `ModelServerHelper`: prompt cleanup (trim, strip control characters, enforce a length limit), history entries validated (role in
  system, user, assistant; capped count), then chat (U2). Tests for cleanup, history validation, and chat delegation. Tool with tests.
- [ ] U8. Shared path guard decision: move `WorkspacePathGuard` from `FileEditHelper` into `AiDevHelpers.Common` if the editing tools need it, with tests moved too.
  Ask the user before moving (it changes an existing helper's references).
- [ ] U9. `code_work` (start and continue): a plan-building session. Decide scope with the user first (it persists session state; where to store it under the repo's
  `.aidevhelpers` folder). Blocked until answered.
- [ ] U10. `fix_code` (enable-analysis, fix-syntax, fix-imports, fix-namespace, format): edits go through `FileEditEngine` with backups; format through the existing
  `ide_format_document` path or `dotnet format` via `CommandRunner`. Blocked on U8 and on the user's choice of format backend.
- [ ] U11. `execute_step` (plan step execution, from `ManagePlan`): depends on U9's plan storage. Blocked on U9.
- [ ] U12. Docs and gates: `docs/features.md`, `docs/self_test.md`, dogfood log; lite gate with aidevtools tools; full gate before any PR to dev/main.

## Answers (2026-11-27)
- `code_work` storage: repo `.aidevhelpers/`. Sessions survive server restarts (persisted to disk).
- `fix_code` format: `ide_format_document`. Needs a running Visual Studio instance, so U10 format steps are blocked when no IDE is open.
- U8: `WorkspacePathGuard` moves into `AiDevHelpers.Common`, with its tests. Unblocks U8 and then U10.
- U9 and U11 can now be planned in detail (storage and persistence are decided).

## Rules
- Each unit: failing test, implement, build, test, commit, push branch. No PR unless the user asks.
- New files are created with Write; existing files edited with TokenSaver `edit_file`.
