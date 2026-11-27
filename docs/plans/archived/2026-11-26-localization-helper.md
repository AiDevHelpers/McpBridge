# LocalizationHelper: implementation plan, 2026-11-26

Status: done. Implemented by the impl plan (revision 2, after the Sonnet review of revision 1, section 7). Each unit is one commit with its tests written first.

## 1. Scope

A new `LocalizationHelper` (`src/Helpers/LocalizationHelper`) that owns everything about resx localization, wired in like the other
helpers. Today the pieces are split: `resx_check` and `string_scan` live in `LintHelper` (`ResxChecker.cs`, `StringScanner.cs`), and
every helper has its own `Strings.cs` over `Resources/Strings.resx`. Nothing handles non-neutral cultures.

Decided by the user:

- The helper absorbs `resx_check` and `string_scan`. Tool names and JSON shapes stay the same, so callers and the lite gate keep working.
- Capabilities: cross-culture key sync, hardcoded string extraction, pseudo-localization, culture and format checks.
- Translations may come from a model, always plan then confirm.
- `LintHelper` references `LocalizationHelper` so `lint_check` keeps its duplicate-resource-name check unchanged.
- Culture checks use `Microsoft.CodeAnalysis.CSharp` (syntax plus semantic model), a new package in the helper.
- Missing-key stubs copy the neutral text with a `needs-translation` comment.

Out of scope: non-resx formats (JSON, PO, XLIFF), VB/F#, localizing the VS Code extension's TypeScript strings.

## 2. Decisions

- Pure engines in the helper library, thin tool class in `src/MCP/Core/src/LocalizationTools.cs`, like `LintTools`.
- Every write is plan, then confirm: without `confirm` the tool returns what it would change and changes nothing.
- **Never invent text by default.** Stubs for missing keys copy the neutral text and set the resx `comment` to `needs-translation`.
  Only the explicit `translate` step produces new text, and it is the single place a model is used.
- **Model step uses MCP sampling** (`sampling/createMessage` on the connected client), so the server holds no API key. If the client
  does not advertise sampling, `localization_translate` refuses with a clear message; no fallback path.
- Translations are written only after `confirm`, and each result is marked `machine-translated` in the resx comment so a human can find and review them.
- Only keys listed in the plan call are translated; placeholders (`{0}`) and named tokens must survive. A translation that changes the
  placeholder set is rejected and left as a stub.
- Resx reading and writing uses `System.Xml.Linq` and keeps the file's header, `xml:space`, and comment order; a write that changes
  nothing else must produce a minimal diff. No `ResXResourceWriter` (reorders, rewrites the header).
- Pseudo-localization is a generated build-time-only culture (`qps-ploc`), written to a file the caller names, never committed by default.

## 3. Cross-cutting contracts

- Project model: a "resource set" is `Resources/Strings.resx` plus every `Strings.<culture>.resx` beside it. A culture is valid only if
  `CultureInfo.GetCultureInfo` accepts it.
- Finding JSON: `{ resx, kind, key, culture? }`. Existing kinds (`duplicate`, `missing`, `unused`) are unchanged; new kinds are
  `untranslated`, `extra`, `placeholder-mismatch`, `culture-call`.
- Every tool takes `root` (folder) and returns `{ ok, count, findings }` or `{ ok:false, error }`, like `LintTools`.
- User-facing text of the helper itself goes in its own `Resources/Strings.resx` (the repo rule that `resx_check` enforces).

## 4. Units

### Unit 1: Create the helper project and move the scanners (refactor, no behaviour change)
- New `src/Helpers/LocalizationHelper/{src,tests}` csproj files, copied from `LintHelper` (net10.0, `SelfTest.md` embedded as
  `AiDevHelpers.SelfTest.md`, `Strings.cs`, `Resources/Strings.resx`).
- Move `ResxChecker`, `StringScanner` and their tests (`StringToolsTests.cs`) out of `LintHelper`. `LintService.CheckResxDuplicates`
  (LintService.cs:46) keeps calling `ResxChecker` through a new `LintHelper` -> `LocalizationHelper` project reference.
- `Strings` is internal per helper and both moved classes use `Strings.Format("FolderNotFound")`: the new helper gets its own
  `FolderNotFound` key; LintHelper keeps its own (LintService.cs:26).
- New `LocalizationTools` class with `ResxCheck` and `StringScan`, same names, descriptions and JSON; remove them from `LintTools`.
- Units 1 and 2 are one commit: `SelfTestCoverageTests` and the lite gate are red between them (the tool class needs its `HelperNames` entry).
- Test first: the moved tests run against the new project and fail to compile until the move; `HardCodedStringTests` and `LintServiceTests` stay green.

### Unit 2: Wire the helper in like every other helper (same commit as unit 1)
- `AiDevHelpers.slnx` (folder `/Helpers/LocalizationHelper/` with src and tests projects).
- `src/MCP/Core/src/AiDevHelpers.MCP.Core.csproj` project reference; `McpServiceCollectionExtensions` `.WithTools<LocalizationTools>()` (:93).
- `src/MCP/Core/tests` project reference to the helper; `HardCodedStringTests.cs:1` `using` changes to `AiDevHelpers.Helpers.LocalizationHelper`.
- `SelfTestTools.HelperNames` (:17) and `SelfTestCoverageTests.ToolToHelper` (`LocalizationTools` -> `LocalizationHelper`); the assembly
  name must be `AiDevHelpers.Helpers.LocalizationHelper` (`SelfTestTools.AssemblyOf`). `LintHelper/SelfTest.md` (lines 3, 14-21) drops the
  moved tools; `LocalizationHelper/SelfTest.md` names them.
- `.github/workflows/ci.yml:144-157`: add a `dotnet pack` line for LocalizationHelper, ordered before LintHelper (which now references it).
- `hardcoded-strings-allowlist.txt` is keyed `Helper|literal`: re-key moved `LintHelper|` rows to `LocalizationHelper|`, and add rows for new literals.
- Hand-edited docs (no generator or check exists): `src/Common/src/Resources/mcp-tools.md` (and `mcp-self-test.md` if it lists tools),
  `docs/features.md:39`, `docs/ide-bridge.md:112-114`, `README.md:18` plus a new helper line, `CHANGELOG.md`, `.claude/reference/lite-gate.md` step 3.
- `full-gate.md` needs no change (it runs the slnx).
- Test first: `SelfTestCoverageTests` fails until the self-test names every moved and new tool.

### Unit 3: Cross-culture key sync (`localization_sync_check`, `localization_sync`)
- Engine `CultureSyncChecker`: for each resource set, compare each `Strings.<culture>.resx` to the neutral one. Report `missing`
  (key in neutral, not in culture), `extra` (in culture, not neutral), `untranslated` (value equal to neutral, or comment
  `needs-translation`), `placeholder-mismatch` (different `{n}` set).
- `localization_sync` plan/confirm: adds missing keys as stubs (neutral text, `needs-translation` comment) and, with `removeExtra`, removes extras. Creates the culture file if
  `culture` names a new valid culture.
- `ResxChecker` matches `Strings.resx` exactly today; its used/unused logic must stay correct once `Strings.<culture>.resx` files exist beside it.
- The writer preserves each file's existing line endings (the repo stores LF); a write that changes nothing else is a zero-line diff.
- Test first: fixture resx sets in temp folders: all four finding kinds, stub write round-trip with a minimal diff (LF and CRLF fixtures), invalid culture refused.

### Unit 4: Hardcoded string extraction (`localization_extract`)
- Builds on `StringScanner` findings. Plan returns, per literal, a proposed key (`PascalCase` from the text, deduplicated against
  existing keys) and the exact `Strings.Get("Key")` replacement. Confirm adds the key to the neutral resx and rewrites the literal
  in the `.cs` file. Interpolated strings become `Strings.Format` with `{0}` placeholders; anything it cannot rewrite safely is
  listed as `skipped` with the reason, never guessed.
- `Strings` is a per-project internal class. The engine finds the nearest `Resources/Strings.resx` walking up from the `.cs` file and
  refuses (as `skipped`) files in a project that has no `Strings.cs` and resx.
- The caller may pass `keys` to override proposed names. Tests first: plain, interpolated, duplicate text, name collision, skipped cases.

### Unit 5: Culture and format checks (`localization_culture_check`)
- Uses `Microsoft.CodeAnalysis.CSharp` (decided): parse each file and use a `CSharpCompilation` with the framework references for the semantic
  model, so `ToString()` is flagged only on numeric and date types. Add the package to the helper (dependency-selection skill: pin the
  version, note why; Roslyn elsewhere in the repo is only the VS installer's, `ExcludeAssets="runtime"`, and is not referenceable here).
- A separate `RoslynHelper` plan exists (`docs/plans/2026-11-26-roslyn-helper.md`, branch `docs/roslyn-helper-plan`, not on `wip` yet): when
  it lands, use the same Roslyn package version, and check whether its workspace can serve this unit instead of a second `CSharpCompilation`.
- Flags `string.Format`, `int/double/DateTime.Parse/TryParse`, `ToUpper/ToLower`, `string.Compare` and `StartsWith/EndsWith/IndexOf(string)`
  without a `CultureInfo`/`StringComparison` argument. Kind `culture-call`; the finding names the call and suggests `InvariantCulture`,
  `CurrentCulture` or an `Ordinal` comparison.
- Report-only; no auto-fix (the right culture is a decision). Tests first with a fixture file covering each call and its fixed form.

### Unit 6: Pseudo-localization (`localization_pseudo`)
- Engine `PseudoLocalizer`: maps letters to accented forms, pads about 30 percent, wraps in `[` `]`, leaves `{0}` placeholders and
  named tokens untouched. Writes `Strings.qps-ploc.resx` beside the neutral file for the path the caller names.
- Plan/confirm. Tests first: placeholder preservation, length growth, round-trip stays well-formed, idempotent when run twice.

### Unit 7: Model translation (`localization_translate`)
- Plan: lists keys needing translation for `culture` (stubs from unit 3) and the exact prompt per key; nothing is sent. Confirm:
  sends one sampling request per batch (the helper's size cap, default 20 keys), validates each reply (placeholder set equal,
  non-empty, one line unless the source has a line break), writes valid ones with comment `machine-translated`, and returns
  rejected keys with the reason.
- The sampling client sits behind an `ITranslator` interface so the engine is tested with a fake; one thin adapter calls the MCP server's `SampleAsync`.
- Refuses when the client lacks sampling. Tests first: fake translator, placeholder-changing reply rejected, batch split, refusal path.
- Risk: many clients, Claude Code included, do not advertise sampling, so this tool may be unusable in practice. Core uses
  `ModelContextProtocol` 2.4.0 (MCP.Core.csproj:42); `SampleAsync` and `ClientCapabilities.Sampling` exist in the cached 1.4.0 and 2.2.0
  but 2.4.0 was not inspected: confirm the API first. `McpServer` is injected as a tool parameter, hence the `ITranslator` seam.
  No server-side sampling is used anywhere in the repo yet.

### Unit 8: Finish
- Run the lite gate; run `resx_check`, `string_scan`, `lint_check` on the repo with the new tools (dogfood), including the new cross-culture checks.
- Update `docs/dogfood.md` tool-use log for each new tool. Record gaps in the gaps table.
- Archive this plan.

## 5. Order and risks

Units 1 then 2 first (small, mechanical, keep the gate green), then 3 to 7 in any order; 7 depends on 3's stubs. Risks:
the move in unit 1 breaking `LintService`'s duplicate check; resx write fidelity (unit 3 test pins it); sampling support varying by
client (unit 7 refuses cleanly); `string_scan`/`resx_check` callers in docs and the lite gate (names are unchanged, so none should break).

## 6. Branch and commits

Branch `feat/localization-helper` from `wip`; PR into `wip`. One commit per unit, each after the lite gate. Per `CLAUDE.md`, a finished unit's PR merges into `wip` without asking.

## 7. Sonnet review, revision 1

Verdict: sound in structure, needed revision. Folded in: LintHelper reference decision and `FolderNotFound` keys (unit 1), the full
wiring list (unit 2: ci.yml pack, MCP Core test reference and `using`, allowlist re-keying, CHANGELOG, lite-gate, ide-bridge, hand-edited
mcp-tools.md), units 1 and 2 merged, the Roslyn premise corrected (the scanner is line-by-line regex, not a syntax tree) and decided
(unit 5), `Strings` lookup rule (unit 4), line-ending preservation (unit 3), and the sampling-support risk (unit 7).
Answered by the user: LintHelper references LocalizationHelper; Roslyn package for culture checks; stubs copy neutral text.
