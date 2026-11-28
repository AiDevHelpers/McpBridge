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
- [x] U2. `ModelServerHelper` generate and chat. (done: red run was a missing-type compile error; green 40/40 in ModelServerHelper tests; class is `ModelServerGenerator`) Failing tests first with the existing fake `HttpMessageHandler` pattern: request body shape (`/api/generate`, `/api/chat`
  as the router documents them; check the router's contract doc in `F:\aidevhelpers\TokenSaver\TokenSaver.SimpleModelServer` before writing), non-2xx, timeout, cancellation,
  cap. Done: red then green.
- [x] U3. Endpoint listing: DROPPED (2026-11-28). The router documents no endpoint list (its GET routes are `/health`, `/health/ready`, `/api/capacity`; `docs/Server/router-contract.md`). Models and loaded models are already reported by `model_server_status`, so `model_endpoints` is not added in U4.
- [x] U4. `model_generate` and `model_chat` tools (U2) in MCP.Core, tool tests (refusals and JSON shape), self-test entries, docs wiring. `model_endpoints` is not added (U3 dropped). (done: MCP.Core 459/459; ModelGenerateToolsTests 5 new)
  Note: `list_ollama_endpoints` is the TokenSaver name; the retire list maps it to `model_server_status`.
- [x] U5. (done 2026-11-28, decision: the route table is supplied by the caller, mapping keys to model names; no router endpoint selection. Tests: 4 helper, 2 MCP; `RouteChoice` record, `model_route` tool.) `model_route` in `ModelServerHelper`: routing keys code, chat, summarize, embed, rerank mapped to a named endpoint or the default; unknown key refused.
  Tests for each key and unknown key. Tool in MCP.Core with tests.
- [x] U6. (done 2026-11-28: decided in `SourceControlHelper`, which already owns `git diff`; `DiffStatParser` and `SummarizeDiffAsync` with a path guard; 9 helper tests; `diff_summarize` in `DiffSummarizeTools` with 9 MCP tests. Summary step reuses the model type selection; no model means stats only.) `diff_summarize` in `SourceControlHelper` (decided in U6 by checking that project's existing scope). Diff through `CommandRunner`
  (`git diff`, `--staged` optional), path guard, returns the diff stats and the raw diff size; the summary step uses U2 generate when present, otherwise returns
  diff stats only with a note. Tests with a fake runner. Tool with tests.
- [x] U7. (done: 6 helper tests and 2 MCP tests; cleanup, history cap at 20 with `historyTrimmed`, prompt cap 20,000) `clean_and_chat` in `ModelServerHelper`: prompt cleanup (trim, strip control characters, enforce a length limit), history entries validated (role in
  system, user, assistant; capped count), then chat (U2). Tests for cleanup, history validation, and chat delegation. Tool with tests.
- [x] U8. (done 2026-11-28: `WorkspacePathGuard` and `PathCheck` moved to `AiDevHelpers.Common` with their own `Strings` resx; tests moved to `src/Common/tests`; `FileEditHelper` references Common. Common 120/120, FileEditHelper 49/49, MCP Core 477/477.) Shared path guard: move `WorkspacePathGuard` from `FileEditHelper` into `AiDevHelpers.Common`, with tests moved too.
  Approved in Answers (2026-11-27).
- [x] U9. (done 2026-11-28: `CodeWorkStart`, `CodeWorkContinue`, `CodeWorkConfirm` in MCP Core, sessions in `CodeWorkStore` under `.aidevhelpers/code-work`, `CodeWork:Enabled` off by default; 11 tests; MCP Core 488/488.) Scope decided by the answers in "Answers" below. The session store and tools are in MCP Core for now, not a new helper project (deviation from the wiring list; move later if needed). Not verified live: start with confirm needs an open Visual Studio instance.
- [x] U10. (done 2026-11-28: `FixCodeTools` in MCP Core, 9 tests, MCP Core 513/513. `enable-analysis` writes the managed IDE0005 block through `FileEditService` with a backup; `format` calls the bridge `formatDocument` (active document, needs a live Visual Studio, not run). `fix-syntax`, `fix-imports` and `fix-namespace` refuse and name `ide_code_cleanup` / `ide_rename_namespace`.) Format backend answered: `ide_format_document`.
- [x] U11. (done 2026-11-28: `CodeWorkExecute` in MCP Core runs one step per call and stores each step's status in the session: pending → created (bridge `addProjectItem`) → drafted → written (confirm). 6 tests; MCP Core 519/519; VS build of AiDevHelpers.slnx passes with 0 errors.) Runs on U9's plan storage, so the step belongs to a `code_work` session, not a separate plan store.
- [x] U12. (done 2026-11-28, lite gate only: `docs/features.md` and `docs/self_test.md` cover the new tools; dogfood log updated; lint 0, localization sync 0, MCP.Http 14/14; `resx_check` 93 unused warnings. Full gate not run: it needs the user's confirm and no PR is open.) Full gate before any PR to dev/main.

## Answers (2026-11-27)
- `code_work` storage: repo `.aidevhelpers/`. Sessions survive server restarts (persisted to disk).
- `fix_code` format: `ide_format_document`. Needs a running Visual Studio instance, so U10 format steps are blocked when no IDE is open.
- U8: `WorkspacePathGuard` moves into `AiDevHelpers.Common`, with its tests. Unblocks U8 and then U10.
- U9 and U11 can now be planned in detail (storage and persistence are decided).
- `code_work` answers (2026-11-28): the plan is passed by the calling AI to `start`. The skeleton files are created from Visual Studio item templates through the IDE
  bridge (`ide_list_item_templates`, `ide_add_project_item`); no IDE open means start refuses. `continue` writes each step's content through `FileEditEngine`
  with a backup, but the content is drafted by the local model (router model type) and the calling AI checks each draft before it is written (two steps: draft, then confirm with
  a token). Server setting `CodeWork:Enabled`, default off; when off, every action returns AUTO_DISABLED.

## Change 2026-11-28: model types as server settings, with escalation
- Model types are not built in. Each type is a server setting: `ModelServer:ModelTypes:<type>` with `Target` and `Model`.
  Read through `IOptions<ModelServerOptions>` (bound from the `ModelServer` section); `ModelServerTools` becomes an instance tool like `IdeTools`.
- `Target` is `router` (run on the SimpleModelServer router, using Ollama with `Model`) or `chat` (hand back to the calling chat: the tool
  does not call the router and returns `handoff` with `model` and a `prompt` that asks chat to use that model).
- `model_generate`, `model_chat` and `clean_and_chat` take `modelType` or `model` (exactly one). A `router` type runs as before; a `chat` type returns `handoff`.
- `model_route` keeps returning the resolved target and model without calling anything.
- U6 onward follow this. U3 stays dropped.

## Rules
- Each unit: failing test, implement, build, test, commit, push branch. No PR unless the user asks.
- New files are created with Write; existing files edited with TokenSaver `edit_file`.
