# Plan: SimpleModelServer connection and file editor in aidevtools

Branch: `feat/modelserver-file-edit` (from `wip`). Worktree: `F:\aidevhelpers\wip-worktree`.
Source of behavior: TokenSaver repo `F:\aidevhelpers\TokenSaver` (read only; its code stays untouched, no removal, no forwarders).
Sonnet review 2026-11-27 folded in (findings noted per unit).

## Source files (TokenSaver, read only)
- Editor: `TokenSaver.Core/TokenSaver.CodeHelper/Patching/PatchEngine.cs`, `WorkspacePathGuard` (`TokenSaver.Common`, `Utilities/Services`),
  `TokenSaver.Host/TokenSaver.MCP/Tools/CodeHelperTools.cs` lines 306-527, tests `TokenSaver.Host/TokenSaver.MCP.Tests/Tools/CodeHelperToolsTests.cs`, `PatchEngineTests.cs`.
- Connection: `TokenSaver.Core/TokenSaver.OllamaIntegration/Services/{SimpleModelServerCapacityClient,OllamaService}.cs`,
  `TokenSaver.Host/TokenSaver.MCP/HealthChecks/SimpleModelServerHealthCheck.cs`, `SimpleModelServer.Config/DefaultConfig.cs`.

## Decisions
- Connection = SimpleModelServer router (default `http://localhost:11435`), HTTP only, no SimpleModelServer assemblies.
- Editor = `PatchEngine` behavior (apply, insert, delete, rename, rollback). Base branch `wip` (user, 2026-11-27).
- Unit 3: status and capacity only (no generate/chat). Global `~/.claude` files are the user's; dogfood log rows still get added (user rule).
- `file_edit` and model-server tools return JSON `{ok,error,...}` (house style, like `BookmarkTools`), not TokenSaver's glyph text.
- Out of scope: `IFileReindexer` reindexing (no aidevtools counterpart), nvidia-smi GPU stats.
- ProgramData discovery is aidevtools design, not a port: read only `routerHost`/`routerPort`; accept loopback hosts only; never echo the file
  or `TrustedApiKeys`; malformed or missing file falls back to `http://localhost:11435`.
- Capacity/status: never cache a failure; success cached with a TTL; explicit HttpClient timeout; honors cancellation.
- Wiring per helper (from BookmarkHelper): csproj with `PackageId` and embedded `SelfTest.md` (`LogicalName="AiDevHelpers.SelfTest.md"`), `Strings.cs` +
  `Resources/Strings.resx` (lite gate: `resx_check`, `string_scan`, `localization_sync_check`), test csproj, `.slnx` entries in `/Helpers/<Name>/`,
  `ProjectReference` in `AiDevHelpers.MCP.Core.csproj`, `WithTools<>` at `McpServiceCollectionExtensions.cs:92`, name in `SelfTestTools.HelperNames`
  (assembly must be `AiDevHelpers.Helpers.<Name>`), pair in `SelfTestCoverageTests` (each tool method name must appear in that `SelfTest.md`),
  tool list in `src/Common/src/Resources/mcp-tools.md`.

## Units
- [x] U1. Contracts. `docs/Host-MCP/model-server-contract.md` and `file-edit-contract.md` from the source files above: endpoints (`/api/capacity`,
  `/api/tags`, `/api/ps`, router `/health`, `/health/ready`), defaults, error text, EditFile actions (trimmed, case-insensitive), `position` default after,
  dryRun, occurrence errors, RequireAnchorMatch, BOM/encoding, atomic write, backup naming. Done: every action/endpoint documented, checked against source.
- [x] U2. (done: red run = CS0103 `WorkspacePathGuard` missing, then 9/9 green; root is an explicit argument, repo-root lookup happens in the tool in U6) Workspace path guard in `FileEditHelper` (new project). Failing tests first (ported from `PatchEngineTests`): `..` escape, absolute outside root,
  no root known, junction/symlink escape, rollback source not in backup root, rollback target outside root. Root comes from a `root` param, else
  `RepoSettingsFile.FindRepoRootFromFolder`. Done: red then green.
- [x] U3. (done: red = CS0246 `ModelServerClient` missing, then 23/23 green; non-loopback host ignores the whole file) `ModelServerHelper` + tests: discovery rules above, health, capacity parse (`gpu.detected`, `Math.Max(1,…)`), non-2xx, malformed JSON, `/api/ps` failure
  swallowed, no failure caching, TTL, timeout, cancellation. Fake `HttpMessageHandler`. Full wiring per list above.
- [x] U4. (done: red = CS0234 missing ModelServerHelper ref; green 376/377 in MCP.Core.Tests. The 1 failure, `HardCodedStringTests` on `RoslynHelper/ActionDispatcher.cs:18`, also fails on base `wip` 45f5848, not caused by this work. Also wired `docs/ide-bridge.md`, `ci.yml` pack step for both helpers, README) `ModelServerTools` (`model_server_status`, `model_server_capacity`): tool tests (ok/error JSON), self-test entries, `mcp-tools.md`.
- [x] U5. (done: red = CS0246 `FileEditEngine` missing, then 42/42 green incl. guard tests; rollback source must be inside the backup folder, stricter than TokenSaver which also accepted any workspace file) `FileEditHelper` edit engine + tests: apply (occurrence 0/N, not found), insert before/after (+anchor fallback when not required), delete literal/regex
  (Multiline, "Invalid regex"), rename (whole word; comment lines skipped unless `replaceInComments`), rollback, dryRun writes nothing, backup per write with
  ms+guid name, BOM/UTF-16 preserved, atomic write with `File.Replace` retry and no `.tmp` left, batch stops at first failure. Backup folder `.aidevhelpers/backups`.
- [x] U6. (done: helper `FileEditService` validates requests, red = CS0246 then 58/58 helper tests green; MCP.Core.Tests 381/382, the 1 failure is the same pre-existing RoslynHelper one) `FileEditTools` (`file_edit`, `action` param, EditFile parameters, JSON results): tool tests, self-test entries, `mcp-tools.md`.
- [ ] U7. Docs and gates: `docs/features.md`, `docs/self_test.md`, repo docs naming `mcp__TokenSaver__edit_file`, dogfood tool-use rows; lite gate
  (`.claude/reference/lite-gate.md`, includes `project_audit`); full gate before any PR to dev/main.

## Rules
- Each unit: failing test, implement, build, test, commit, push branch. No PR to `wip` unless the user asks.
- Code edits go through TokenSaver `edit_file` where aidevtools does not cover them.
