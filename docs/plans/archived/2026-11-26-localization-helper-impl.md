# LocalizationHelper: implementation plan, 2026-11-26

Status: done (units 1 to 8 merged into wip as PRs 46 to 51 and this one). Open questions answered (last section). Source: `2026-11-26-localization-helper.md` (revision 2, includes the Sonnet review in its section 7).
Checked against `src/Helpers/LintHelper/src/{LintService,ResxChecker,StringScanner,Strings}.cs`, its csproj and `SelfTest.md`,
`src/MCP/Core/src/{LintTools,SelfTestTools,McpServiceCollectionExtensions}.cs`, `src/MCP/Core/tests/Tools/{SelfTestCoverageTests,
HardCodedStringTests,DocsMatchCodeTests}.cs`, `hardcoded-strings-allowlist.txt`, `.github/workflows/ci.yml`, `AiDevHelpers.slnx`,
`.claude/reference/lite-gate.md`, and the house style of `2026-11-25-project-scaffolding-impl.md`.

Branch `feat/localization-helper` from `wip` (already checked out). Units: 1+2 (one commit), 3, 4, 5, 6, 7, 8 (finish). Each unit:
tests first and run red, then code, then the lite gate, then one commit, push, PR into `wip`, merge into `wip` (repo `CLAUDE.md`),
then bring the branch level with `wip` before the next unit. Edits to code files go through TokenSaver `edit_file` in the main
session. No Claude attribution lines in any commit or PR text. LF everywhere. Commit message via a file and `git commit -F`.

## Findings that settle the design's open items

- `ResxChecker` globs `"Strings.resx"` exactly, so `Strings.de.resx` is never read as a neutral file today. Unit 3 adds a regression
  test; no code change is needed there.
- `hardcoded-strings-allowlist.txt` is at `src/MCP/Core/tests/Tools/hardcoded-strings-allowlist.txt` (not the repo root) and holds no
  `LintHelper|` rows, so there is nothing to re-key in unit 2. `HardCodedStringTests` scans every folder under `src/Helpers` and picks
  up the new helper by itself; new allowlist rows are added only if a new multi-word literal is not user-facing.
- `DocsMatchCodeTests` enforces three things for the new helper: each MCP tool named in backticks in `docs/ide-bridge.md` (snake_case
  or C# name), the project in `AiDevHelpers.slnx`, and the helper's csproj path in the `ci.yml` Pack step (checked only when the
  csproj has `<IsPackable>true</IsPackable>`). `docs/features.md` and `mcp-tools.md` need no test but are updated by hand.
- `StringFinding` is `(File, Line, Literal, ResxKey)` and has no column or interpolation flag; unit 4 adds `Column` and `Interpolated`
  and updates the moved tests (no shim).
- `LintHelper` has no project reference today; it gains one to `LocalizationHelper` (so the ci.yml pack order puts LocalizationHelper first).
- Existing `Strings.cs` pattern: `ResourceManager("AiDevHelpers.Helpers.<Helper>.Resources.Strings", ...)`, `Get`/`Format`, internal.
- The repo has no culture resx anywhere, so every new check reports zero findings on this repo until one is added.

## Shared engine contracts (created in unit 3, used by 4, 6, 7)

All in `src/Helpers/LocalizationHelper/src`, namespace `AiDevHelpers.Helpers.LocalizationHelper`.

- `ResourceSet` (static): `IReadOnlyList<ResourceSetInfo> Find(string root)`. `record ResourceSetInfo(string NeutralPath, string Dir,
  IReadOnlyDictionary<string,string> CulturePaths, IReadOnlyList<string> InvalidFiles)`. A set is a `Strings.resx` in a `Resources`
  folder plus `Strings.<culture>.resx` beside it; `bin`/`obj` skipped (`StringScanner.InBuildFolder`, made `public`). A culture name
  `CultureInfo.GetCultureInfo` rejects goes to `InvalidFiles`, reported as finding kind `invalid-culture`. `qps-ploc` is accepted by
  name (the pseudo culture, unit 6) and skipped by the sync check.
- `ResxFile`: `static ResxFile Load(string path)`; `IReadOnlyList<ResxEntry> Entries`; `record ResxEntry(string Name, string Value,
  string? Comment)`; `void Add(string name, string value, string? comment)`; `bool Remove(string name)`; `void SetValue(string name,
  string value, string? comment)`; `void Save()`; `static ResxFile CreateFrom(ResxFile neutral, string path)` (copies the header
  and `resheader` elements, no data). Uses `XDocument` with `LoadOptions.PreserveWhitespace`, detects the file's newline style
  (LF when new) and BOM and writes them back, copies the indentation of the last `<data>` element, writes each entry as
  `<data name=".." xml:space="preserve"><value>..</value><comment>..</comment></data>`. Load, no edit, save is byte-identical.
- `Placeholders.Of(string text)`: `IReadOnlySet<string>` of `{0}`-style and `{name}` tokens (regex
  `(?<!\{)\{[A-Za-z0-9_]+(?:[:,][^{}]*)?\}(?!\})`; `{{` and `}}` are literal braces). Equal sets mean equal placeholders.
- `PlanStore<T>`: `string Add(T plan)` returns a 32-hex token; `bool TryTake(string token, out T plan)` single use; 15-minute expiry;
  injectable clock for tests (same shape as the pending-token map in `ProjectAdder`).
- Plan reply (every write tool, no `confirm`): `{ ok:true, plan:true, token, expires_utc, count, changes:[...] }`. Confirm reply
  (`confirm` = that token): `{ ok:true, applied:true, count, files:[relative paths] }`. Failure: `{ ok:false, error }`. Unknown, used
  or expired token: `ok:false`, `Strings.resx` key `TokenInvalid`. Confirm re-reads each target file and refuses when its SHA-256
  differs from the plan's (key `FileChangedSincePlan`).

## Unit 1+2: Create the helper, move the scanners, wire it in (one commit)

Goal: `LocalizationHelper` exists, owns `resx_check` and `string_scan` with unchanged names, descriptions and JSON; the gate stays green.

Create:
- `src/Helpers/LocalizationHelper/src/AiDevHelpers.Helpers.LocalizationHelper.csproj`: copy of LintHelper's (net10.0, `PackageId`
  `AiDevHelpers.Helpers.LocalizationHelper`, `IsPackable` true, `Nerdbank.GitVersioning` `Update` 3.10.94, `SelfTest.md` embedded as
  `AiDevHelpers.SelfTest.md`, new `Description`). Assembly name stays the default (needed by `SelfTestTools.AssemblyOf`).
- `src/Helpers/LocalizationHelper/src/Strings.cs` (resource base `AiDevHelpers.Helpers.LocalizationHelper.Resources.Strings`),
  `Resources/Strings.resx` with `FolderNotFound` ("Folder not found: {0}"), header copied from LintHelper's resx.
- `src/Helpers/LocalizationHelper/src/SelfTest.md`: `Tools:` line naming `StringScan`, `ResxCheck`; the "StringScan and ResxCheck"
  section moved from LintHelper's file verbatim.
- `src/Helpers/LocalizationHelper/tests/AiDevHelpers.Helpers.LocalizationHelper.Tests.csproj`: copy of LintHelper's tests csproj
  (xunit 2.9.3, Microsoft.NET.Test.Sdk 18.12.0, runner 4.0.1) with the project reference swapped.
- `src/MCP/Core/src/LocalizationTools.cs`: `[McpServerToolType] internal sealed class LocalizationTools` with `public static string
  StringScan(string path)` and `public static string ResxCheck(string root)`; bodies, `[Description]` text and JSON exactly as in
  `LintTools` today (private `Json` helper copied).
- `src/MCP/Core/tests/Tools/LocalizationToolsTests.cs` (new): tests below.

Move (`git mv` by the main session, then namespace edit to `AiDevHelpers.Helpers.LocalizationHelper`): `ResxChecker.cs`,
`StringScanner.cs`, `tests/StringToolsTests.cs`. Public signatures unchanged; `StringScanner.InBuildFolder` becomes `public`.

Change:
- LintHelper csproj: `ProjectReference` to LocalizationHelper. `LintService.cs`: add `using AiDevHelpers.Helpers.LocalizationHelper;`
  (line 46 call unchanged). LintHelper keeps its own `FolderNotFound`.
- `src/MCP/Core/src/LintTools.cs`: delete `StringScan` and `ResxCheck` and the now-unused usings.
- `AiDevHelpers.MCP.Core.csproj`: `ProjectReference` to LocalizationHelper. `McpServiceCollectionExtensions.cs`: `.WithTools<LocalizationTools>()`
  after `.WithTools<LintTools>()`.
- `SelfTestTools.HelperNames`: add `"LocalizationHelper"`. `SelfTestCoverageTests.ToolToHelper`: add `("LocalizationTools", "LocalizationHelper")`.
- `src/MCP/Core/tests/AiDevHelpers.MCP.Core.Tests.csproj`: `ProjectReference` to LocalizationHelper; `HardCodedStringTests.cs:1` `using`
  becomes `AiDevHelpers.Helpers.LocalizationHelper` (it uses only `StringScanner`).
- `AiDevHelpers.slnx`: folder `/Helpers/LocalizationHelper/` with the src and tests projects, after the LintHelper folder.
- `.github/workflows/ci.yml` Pack step: a `dotnet pack` line for LocalizationHelper directly before LintHelper's; comment notes LintHelper depends on it.
- `src/Helpers/LintHelper/src/SelfTest.md`: `Tools:` becomes `LintCheck` only; delete the "StringScan and ResxCheck" section.
- Docs by hand: `src/Common/src/Resources/mcp-tools.md` rows 120-121 (names unchanged; move under a Localization heading if the file groups by helper,
  read it first); `docs/ide-bridge.md:113-114` Helper column `LintHelper` to `LocalizationHelper`; `docs/features.md:39` becomes `Lint: lint_check.`
  plus `- Localization: string_scan, resx_check.`; `README.md:18` drops the scanner and resx check from the LintHelper line and adds a
  `src/Helpers/LocalizationHelper` line; `CHANGELOG.md` section "Localization helper (unreleased)": the two tools moved, same names and JSON.

Tests first:
- Moved `StringScannerTests` and `ResxCheckerTests`: assertions unchanged. Red: the new tests project does not compile before the helper exists.
- `LocalizationToolsTests.ResxCheck_ReportsADuplicate`: `H/src/Resources/Strings.resx` with `One` twice; asserts `ok` true, `count` 1, `kind` `duplicate`, `key` `One`.
- `LocalizationToolsTests.StringScan_ReportsALiteral`: `A.cs` with `var a = "Could not read the file.";`; one finding, `line` 1.
- `LocalizationToolsTests.BothToolsReturnOkFalseForAMissingFolder`: `error` contains the folder.
- Fail until wired: `SelfTestCoverageTests.EveryHelperEmbedsASelfTest`, `EveryMcpToolIsNamedInItsHelpersSelfTest`;
  `DocsMatchCodeTests.EveryHelperIsPackedByTheTagBuild`, `EveryHelperProjectIsInTheSolution`.
- Green throughout (asserted in the record): `LintServiceTests` (duplicate resource name still reported through the new reference), `HardCodedStringTests`.

Red-run record: before any wiring, compile errors (namespace and project missing). With the projects present but self-test, ci.yml and slnx not done,
expect exactly the four failing tests above. Put the actual list in the commit body.

Lite gate before the commit (`.claude/reference/lite-gate.md` steps 1-5): `ide_list_instances`, `git_status`, `test_affected`, `lint_check`, `resx_check`,
`string_scan` on each changed `.cs`, `project_audit`, `ide_build` of the slnx, `ide_run_tests` then `ide_get_test_results`. Extra: `resx_check` on the repo root
returns the same findings as on `wip` before the move (run it on `wip` first and compare). Close running `AiDevHelpers.MCP` processes by PID before the build.

Commit: `Add LocalizationHelper and move string_scan and resx_check into it`

Who: one Haiku subagent: csproj copies, `Strings.cs`, resx, slnx, ci.yml, README, features.md, ide-bridge.md, mcp-tools.md, CHANGELOG, SelfTest.md splits. Main session:
`git mv` and all git writes, namespace edits, `LocalizationTools.cs`, `LintTools.cs`, `SelfTestTools`, `McpServiceCollectionExtensions`, test code, diff review of
every file the subagent touched, gate, commit.

## Unit 3: Cross-culture key sync (`localization_sync_check`, `localization_sync`)

Create (helper src): `ResourceSet.cs`, `ResxFile.cs`, `Placeholders.cs`, `PlanStore.cs` (contracts above), `CultureSyncChecker.cs`, `CultureSyncWriter.cs`.
Strings.resx keys: `InvalidCulture` ("Not a valid culture name: {0}"), `TokenInvalid`, `FileChangedSincePlan`.

Public API:
- `public sealed record SyncFinding(string Resx, string Kind, string Key, string? Culture)`.
- `public static class CultureSyncChecker { IReadOnlyList<SyncFinding> Check(string root); }` Per set and culture file: `missing` (key in neutral, not in
  culture), `extra` (in culture only), `untranslated` (culture value equals neutral value, or comment `needs-translation`; comment `same-in-culture`
  suppresses the equal-value case), `placeholder-mismatch` (`Placeholders.Of` sets differ), `invalid-culture`. `Resx` is the culture file's path relative to `root`
  (neutral path when the culture file is missing). Order: resx path, kind, key (ordinal).
- `public sealed record SyncChange(string Resx, string Action, string Key, string Culture)`; `Action` is `create-file`, `add-stub` or `remove-extra`.
- `public sealed class CultureSyncWriter(PlanStore<SyncPlan> store)`: `SyncPlan Plan(string root, string? culture, bool removeExtra)`;
  `IReadOnlyList<string> Apply(string token)` returns relative paths written. `culture` null = every existing culture file; a culture with no file plans
  `create-file` plus a stub per neutral key. Invalid culture throws `ArgumentException` (key `InvalidCulture`). Stub: neutral text, comment `needs-translation`.
  Extras are removed only with `removeExtra`.

MCP tools (in `LocalizationTools.cs`):
- `LocalizationSyncCheck(string root)` -> `{ ok, count, findings:[{resx, kind, key, culture}] }`; missing folder -> `{ ok:false, error }`.
- `LocalizationSync(string root, string? culture = null, bool removeExtra = false, string? confirm = null)` -> plan or confirm reply per the shared contracts;
  `changes` is `[{resx, action, key, culture}]`. One static `PlanStore<SyncPlan>`.

Tests first (helper tests, temp folders, shared `ResxFixture` test helper writing a resx from `(name, value, comment)` tuples with LF or CRLF):
- `CultureSyncCheckerTests`: `ReportsMissingKey`, `ReportsExtraKey`, `ReportsUntranslatedWhenValueEqualsNeutral`, `ReportsUntranslatedWhenCommentIsNeedsTranslation`,
  `SameInCultureCommentSuppressesUntranslated`, `ReportsPlaceholderMismatch`, `DoubleBracesAreNotPlaceholders`, `InvalidCultureFileNameIsReported` (`Strings.xx-bogus.resx`),
  `PseudoCultureFileIsSkipped`, `ReportsNothingWhenInSync`, `ResxCheckerIgnoresCultureFiles` (regression: `ResxChecker.Check` on a set with `Strings.de.resx`).
- `ResxFileTests`: `LoadSaveIsByteIdentical` (LF, CRLF, with and without BOM), `AddWritesOneDataElementAndNothingElse` (diff is exactly the added lines), `AddKeepsCrlf`,
  `RemoveWritesAMinimalDiff`, `SpecialCharactersRoundTrip` (`&`, `<`), `CreateFromCopiesHeaderNotData`.
- `CultureSyncWriterTests`: `PlanWritesNothing` (bytes unchanged), `ConfirmAddsStubsWithNeutralTextAndComment`, `ConfirmCreatesNewCultureFile`, `RemoveExtraOnlyWhenAsked`,
  `InvalidCultureRefused`, `TokenIsSingleUseAndExpires`, `ConfirmRefusesWhenFileChangedSincePlan`, `SecondSyncAfterConfirmPlansNothing`.
- `PlaceholdersTests`: `{0}`, `{name}`, `{0:N2}`, `{{literal}}`.
- Core `LocalizationToolsTests`: `SyncCheck_ReturnsTheFindingShape`, `Sync_WithoutConfirmReturnsPlanAndChangesNothing`, `Sync_ConfirmApplies`, `Sync_UnknownTokenIsOkFalse`.

Red-run record: helper tests fail to compile (types missing); with empty type stubs every listed test fails on an assertion, none pass by accident. Record counts.

Wiring in this unit: `SelfTest.md` (fixture: neutral keys A and B; `Strings.de.resx` with A equal to neutral and an extra C; PASS when `localization_sync_check`
reports `missing` B, `extra` C, `untranslated` A; `localization_sync` plan lists `add-stub` B and changes nothing; confirm writes it; a second check shows no `missing`);
`docs/ide-bridge.md` and `mcp-tools.md` rows; `features.md`; `CHANGELOG.md`; `.claude/reference/lite-gate.md` step 3 gains: call `mcp__aidevtools__localization_sync_check`
with the repo root, stop on `missing` or `placeholder-mismatch`, report `untranslated` without blocking. Lite gate as unit 1, then `localization_sync_check` on the repo (expect zero findings).

Commit: `LocalizationHelper: cross-culture key sync check and sync with plan and confirm`

Who: Haiku: `ResxFixture`, `PlaceholdersTests`, doc rows. Main: `ResxFile` (fidelity is the risk), checker, writer, plan store, tool methods, remaining tests, gate, commit.

## Unit 4: Hardcoded string extraction (`localization_extract`)

Change `StringScanner`/`StringFinding`: `record StringFinding(string File, int Line, int Column, string Literal, string? ResxKey, bool Interpolated)`; `Column` is the 0-based
index of the literal's opening quote (or `$`) in the line. Update the moved tests that construct or compare findings.

Create `StringExtractor.cs`:
- `record ExtractItem(string File, int Line, string Literal, string Key, string Replacement)`; `record ExtractSkip(string File, int Line, string Literal, string Reason)`;
  `record ExtractPlan(IReadOnlyList<ExtractItem> Items, IReadOnlyList<ExtractSkip> Skipped)`.
- `public sealed class StringExtractor(PlanStore<ExtractPlan> store)`: `ExtractPlan Plan(string path, IReadOnlyDictionary<string,string>? keys)`; `IReadOnlyList<string> Apply(string token)`.
- Key proposal: first 5 words, letters and digits only, PascalCase, at most 40 characters, a leading digit prefixed `T`; an existing key with the same value is reused (scanner `ResxKey`);
  a name clash with different text gets suffix `2`, `3`...; identical literals in one plan share a key. `keys` maps literal text to the caller's key and wins; a caller key that already
  exists with different text is refused (key `KeyExists`).
- Replacement: plain -> `Strings.Get("Key")`; interpolated with simple holes (`{name}`, `{a.b}`, `{Method()}`, no format or alignment) -> `Strings.Format("Key", name, a.b, Method())`
  with `{0}`, `{1}` in the resx value; the same expression twice gets two placeholders.
- Strings-class rule: nearest `Resources/Strings.resx` walking up (depth 8, as the scanner); its project folder must hold `Strings.cs`, and the file's `namespace` must equal or extend the
  namespace in `Strings.cs`. Otherwise skipped.
- Skip reason codes (stable strings in the JSON `reason`): `no-strings-class`, `namespace-mismatch`, `const-context` (line has `const `), `case-label`, `interpolation-format`
  (format, alignment or nested quotes in a hole), `verbatim-or-raw` (`@"` or `"""`), `unsupported-escape` (anything but `\"`, `\\`, `\n`, `\t`).
- Apply: per file, replace right to left by `Column`; add keys to the neutral resx through `ResxFile.Add`; keep the .cs newline style and BOM; refuse when a touched file's SHA-256 differs from the plan's.

Tool: `LocalizationExtract(string path, Dictionary<string,string>? keys = null, string? confirm = null)` -> plan `{ ok, plan:true, token, expires_utc, count, items:[{file,line,literal,key,replacement}],
skipped:[{file,line,literal,reason}] }`; confirm `{ ok, applied:true, count, files }`.

Tests first (`StringExtractorTests`, temp project: `Strings.cs` declaring `namespace P;`, `Resources/Strings.resx`, `A.cs`): `PlainLiteralBecomesStringsGet`, `InterpolatedLiteralBecomesFormat`,
`RepeatedHoleGetsTwoPlaceholders`, `DuplicateTextSharesOneKey`, `ExistingResxTextReusesItsKey`, `KeyCollisionGetsSuffix`, `CallerKeyOverridesProposal`, `CallerKeyWithDifferentExistingTextRefused`,
`FileWithoutStringsClassIsSkipped`, `NamespaceOutsideProjectIsSkipped`, `ConstLineIsSkipped`, `CaseLabelIsSkipped`, `FormatSpecifierHoleIsSkipped`, `VerbatimIsSkipped`,
`TwoLiteralsOnOneLineRewriteCorrectly`, `ApplyRefusesWhenFileChanged`, `CrlfSourceStaysCrlf`, `PlanWritesNothing`, `AfterApplyScannerFindsNothingNew`, `AfterApplyResxCheckReportsNoMissing`.
Scanner: `FindingCarriesColumnAndInterpolatedFlag`. Core: `Extract_ReturnsItemsAndSkipped`.

Red-run record: compile failure on `Column`/`StringExtractor`, then assertion failures. Record counts.

Wiring: `SelfTest.md` (fixture with one plain and one `const` literal; plan lists one item and one `const-context` skip; confirm rewrites; `StringScan` then reports only the const), `ide-bridge.md`,
`mcp-tools.md`, `features.md`, `CHANGELOG.md`. Strings.resx key `KeyExists`. Lite gate as unit 1; dogfood: `localization_extract` plan on `src/Helpers/LocalizationHelper/src` (expect no items).

Commit: `LocalizationHelper: localization_extract moves hardcoded strings into the resx`

Who: main for extractor and tests (safe source rewriting is the risk). Haiku: `SelfTest.md`, doc rows, the temp-project fixture builder.

## Unit 5: Culture and format checks (`localization_culture_check`)

First step (no user input): check whether the Roslyn helper plan (`docs/roslyn-helper-plan`) has landed on `wip` (`git_status`, `git branch -a`). If so, use its Roslyn version and read whether its
workspace can serve this unit; if not, proceed.

Package: `Microsoft.CodeAnalysis.CSharp` in the helper csproj, exact version, no range. Why: it is the compiler's own syntax tree and semantic model, so `ToString()` is flagged only on numeric and
date types (line regex cannot know the receiver type). Pin: the newest stable version `package_check` finds at unit start (the repo's other Roslyn reference, `Microsoft.CodeAnalysis.CSharp.Workspaces`
5.12.0-1.26573.119 with `ExcludeAssets="runtime"` in the Vs2026 installer, is a VS-matched prerelease and not usable in a shipped library). Run the `dependency-selection` skill (license, maintenance,
transitive `Microsoft.CodeAnalysis.Common`, `System.Collections.Immutable`, `System.Reflection.Metadata`), record the result as an XML comment beside the reference, check for vulnerabilities, and
build the dependents (`LintHelper`, Core, tests) with `-warnaserror` for downgrade warnings.

Create `CultureCallChecker.cs`:
- `record CultureCallFinding(string File, int Line, string Kind, string Call, string Suggestion)`; `Kind` is always `culture-call`; `Call` is the call text as written; `Suggestion` from resx keys
  `CultureSuggestInvariant`, `CultureSuggestCurrent`, `CultureSuggestOrdinal`.
- `public static class CultureCallChecker { IReadOnlyList<CultureCallFinding> Check(string path); }` (file or folder; `bin`/`obj` skipped; missing path throws `DirectoryNotFoundException`, key `FolderNotFound`).
  One `CSharpCompilation` over all parsed files, references from `AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")`. Calls whose symbol does not resolve (types from the project's own references)
  are skipped; the tool description says so. Flag only when no argument is `CultureInfo`, `IFormatProvider`, `StringComparison` or `CompareOptions`: `string.Format`; `Parse`/`TryParse` on int, long,
  double, decimal, DateTime, DateTimeOffset, TimeSpan; no-argument `string.ToUpper`/`ToLower`; `string.Compare`; `string.StartsWith`/`EndsWith`/`IndexOf`/`LastIndexOf` with a string argument;
  no-argument `ToString()` on numeric and date types. `Contains(string)` is ordinal and not flagged. Interpolated strings are not flagged. A trailing `// culture-ok` comment suppresses a line.
- Suggestion: parse/format/ToString/ToUpper -> Invariant or Current; string comparisons -> Ordinal.

Tool: `LocalizationCultureCheck(string path)` -> `{ ok, count, findings:[{file, line, kind:"culture-call", call, suggestion}] }`. Report only.

Tests first (`CultureCallCheckerTests`, fixture `Calls.cs` with each call then its fixed form): `FlagsStringFormatWithoutProvider`, `DoesNotFlagStringFormatWithInvariantCulture`, `FlagsIntParseAndTryParse`,
`DoesNotFlagParseWithCulture`, `FlagsToUpperAndToLower`, `DoesNotFlagToUpperInvariant`, `FlagsStringCompareAndStartsWithString`, `DoesNotFlagStringComparisonOrdinal`, `FlagsToStringOnIntDoubleDateTime`,
`DoesNotFlagToStringOnStringOrUserType` (semantic-model test), `DoesNotFlagContainsString`, `CultureOkCommentSuppresses`, `UnresolvedTypeIsSkipped`, `FolderScanSkipsObj`, `MissingPathThrows`.
Core: `CultureCheck_ReturnsTheFindingShape`.

Red-run record: restore plus compile failure, then assertion failures. Record counts and the pinned version.

Wiring: `SelfTest.md`, `ide-bridge.md`, `mcp-tools.md`, `features.md`, `CHANGELOG.md`. Lite gate as unit 1, plus `localization_culture_check` on `src/Helpers` as dogfood: findings go in the commit body,
not fixed here.

Commit: `LocalizationHelper: culture and format call check using Roslyn`

Who: main: csproj pin, dependency-selection, checker. Haiku: fixture `Calls.cs` text from the rule list, doc rows.

## Unit 6: Pseudo-localization (`localization_pseudo`)

Create `PseudoLocalizer.cs`:
- `public static class PseudoLocalizer { string Transform(string text); }` Maps letters to accented look-alikes by a fixed table in code, skips `{...}` tokens and `{{`/`}}`, pads with `~` to 30 percent
  of the transformed visible length (rounded up, minimum 1), wraps in `[` `]`. Empty text stays empty.
- `public sealed class PseudoWriter(PlanStore<PseudoPlan> store)`: `PseudoPlan Plan(string root, string neutralResx, string? output)`; `string Apply(string token)`. Default output
  `Strings.qps-ploc.resx` beside the neutral file; an explicit `output` must end in `.resx` and resolve inside `root`. Output is generated fresh from the neutral file (all keys, comment
  `pseudo-localized`), so a second run gives identical bytes.
- Strings.resx keys: `OutputOutsideRoot`, `NotAResx`.

Tool: `LocalizationPseudo(string root, string resx, string? output = null, string? confirm = null)` -> plan `{ ok, plan:true, token, expires_utc, count, output, changes:[{key, source, pseudo}] }` (first 20
changes; `count` is the total); confirm `{ ok, applied:true, count, files }`. The tool never commits the file.

Tests first: `PseudoLocalizerTests`: `PreservesNumberedPlaceholders`, `PreservesNamedPlaceholdersAndFormatSpecs`, `PreservesEscapedBraces`, `GrowsTextByAboutThirtyPercent` (1.25x to 1.5x for a 40-char input),
`WrapsInBrackets`, `PlaceholderSetIsUnchanged`, `EmptyStaysEmpty`, `IsDeterministic`. `PseudoWriterTests`: `PlanWritesNothing`, `ConfirmWritesAResxThatXDocumentLoads`, `RunningTwiceGivesIdenticalBytes`,
`OutputOutsideRootRefused`, `NonResxOutputRefused`, `SyncCheckIgnoresThePseudoFile`. Core: `Pseudo_PlanThenConfirm`.

Red-run record: compile failure, then assertion failures. Record counts.

Wiring: `SelfTest.md`, `ide-bridge.md`, `mcp-tools.md`, `features.md`, `CHANGELOG.md`. Lite gate as unit 1. Delete any `Strings.qps-ploc.resx` the dogfood run leaves; `git_status` must not show it.

Commit: `LocalizationHelper: pseudo-localization with plan and confirm`

Who: Haiku: letter table, placeholder cases of `PseudoLocalizerTests`, docs. Main: writer, plan store use, gate.

## Unit 7: Model translation (`localization_translate`)

Step 0 (verify first): confirm the sampling API in `ModelContextProtocol` 2.4.0 (Core's pin, MCP.Core.csproj:42): `McpServer.SampleAsync`, `ClientCapabilities.Sampling`, `CreateMessageRequestParams`.
Only 1.4.0 and 2.2.0 are in the local NuGet cache (2.2.0 has `SampleAsync(CreateMessageRequestParams, CancellationToken)` and an `IEnumerable<ChatMessage>` overload); restore 2.4.0 and read the members
with `ide_go_to_definition`/`ide_semantic_definition` on Core. Record the finding in the commit body. If the API differs from the sketch, change only the adapter.

Create (helper src, no MCP SDK dependency):
- `ITranslator { Task<IReadOnlyDictionary<string,string>> TranslateAsync(TranslationBatch batch, CancellationToken ct); }`; `record TranslationItem(string Key, string Source, string? Comment)`;
  `record TranslationBatch(string Culture, IReadOnlyList<TranslationItem> Items)`.
- `TranslationService(ITranslator translator, PlanStore<TranslatePlan> store, int batchSize = 20)`: `TranslatePlan Plan(string root, string culture, IReadOnlyList<string>? keys)`;
  `Task<TranslateResult> ApplyAsync(string token, CancellationToken ct)`; `record TranslateResult(IReadOnlyList<string> Written, IReadOnlyList<TranslateRejection> Rejected)`;
  `record TranslateRejection(string Key, string Reason)`.
- Plan: candidates are keys of the culture file with comment `needs-translation`; `keys` narrows them and a named key that is not a stub is refused. Per key the plan lists `{key, source, prompt}` and the
  batch count; nothing is sent. Prompt text comes from resx keys `TranslatePromptSystem` and `TranslatePromptItem` (reply must be one JSON object `{"Key":"translation"}`, placeholders and tokens kept exactly).
- Apply: batches of `batchSize`, one `TranslateAsync` each; parse the reply as a JSON object; per key validate present, non-empty, `Placeholders.Of` equal to the source's, no line break unless the source has
  one. Valid -> `ResxFile.SetValue(key, text, "machine-translated")`; invalid stays a stub with reason `missing`, `empty`, `placeholder-mismatch`, `unexpected-line-break`, `unparseable-reply` or `translator-failed`
  (a throwing batch leaves its keys unchanged; other batches still run). Only planned keys are written. Drift refusal as in the shared contracts.
- Strings.resx keys: `SamplingNotSupported` ("The connected client does not support sampling, so translation is not available."), the two prompt keys.

Create (Core): `src/MCP/Core/src/SamplingTranslator.cs`: `internal sealed class SamplingTranslator(McpServer server) : ITranslator` calling `server.SampleAsync`; `internal static bool IsAvailable(ClientCapabilities? caps)`.
Tool: `LocalizationTranslate(McpServer server, string root, string culture, string[]? keys = null, string? confirm = null, CancellationToken ct = default)`. No `confirm`: plan reply `{ ok, plan:true, token,
expires_utc, count, batches, sampling_available, items:[{key, source, prompt}] }` (works without sampling so the cost is visible). With `confirm`: if sampling is unavailable -> `{ ok:false, error }` with
`SamplingNotSupported` and the token is not consumed; else `{ ok, applied:true, count, files, rejected:[{key, reason}] }`. No fallback path.

Tests first (`FakeTranslator`): `PlanListsOnlyNeedsTranslationKeys`, `PlanSendsNothing` (0 calls), `KeysNarrowThePlan`, `NonStubKeyRefused`, `ApplyWritesValidTranslationWithMachineTranslatedComment`,
`PlaceholderChangingReplyIsRejectedAndLeftAsStub`, `EmptyReplyRejected`, `ExtraKeyInReplyIgnored`, `UnparseableReplyRejectsTheBatch`, `BatchesSplitAtTheCap` (45 keys -> calls of 20, 20, 5),
`FailedBatchDoesNotStopOthers`, `OnlyPlannedKeysWritten`, `ConfirmRefusesWhenFileChanged`, `TokenSingleUse`, `AfterApplySyncCheckNoLongerReportsThoseKeysUntranslated`. Core: `IsAvailableFalseWhenCapabilitiesNull`,
`IsAvailableFalseWhenSamplingNull`, `IsAvailableTrueWhenSamplingSet`, `Translate_PlanWorksWithoutSampling`.

Red-run record: compile failure, then assertion failures. Record counts and the verified SDK members.

Wiring: `SelfTest.md` (plan check always; confirm step marked SKIPPED with the reason when the client lacks sampling), `ide-bridge.md`, `mcp-tools.md` (state that the client must support sampling), `features.md`,
`CHANGELOG.md`. Lite gate as unit 1.

Commit: `LocalizationHelper: model translation through MCP sampling, plan and confirm`

Who: main: service, adapter, SDK check. Haiku: `FakeTranslator`, batch-split and placeholder tests from the list, docs.

## Unit 8: Finish

- Lite gate on the branch head, then the full gate (`.claude/reference/full-gate.md`): the set changes packaging (ci.yml) and adds a package.
- Dogfood through aidevtools tools only: `resx_check`, `string_scan`, `lint_check`, `localization_sync_check`, `localization_culture_check` on the repo; in a temp copy, `localization_sync` for a scratch
  culture `fr` (plan, confirm), `localization_pseudo` (plan, confirm), `localization_extract` plan on a scratch file.
- Update `docs/dogfood.md` tool-use log: a row per new tool (`localization_sync_check`, `localization_sync`, `localization_extract`, `localization_culture_check`, `localization_pseudo`,
  `localization_translate`) and the moved `resx_check`/`string_scan`; gaps to the gaps table; gaps needing a tool fix to the backlog.
- Review aidevtools use across the plan: list steps done with a raw command and why, and log them.
- Archive: `git mv` this plan and the design plan to `docs/plans/archived/` (via `git_plan`).
- Commit: `Docs: LocalizationHelper dogfood log and plan archive`. Open the final PR per `pr-promotion` (into `wip`).
- Who: main. Haiku may run the read-only dogfood calls and report replies; it writes nothing.

## Subagent rules for every unit

Haiku subagents get exactly one repo path (`F:\source\McpBridge`), edit only files inside it, never `~/.claude`, `claude_rules` or another repo, run no git writes and no build. The main session reads the
diff of each touched file before building. A Haiku result that fails review is redone in the main session (no sonnet retry without asking).

## Decisions on the open questions

1. Confirm is the single-use token string from the plan call, with the file-drift check (user, 2026-11-26).
2. The resx comment `same-in-culture` suppresses `untranslated` for that key (user, 2026-11-26).
3. Dogfood log: tool-use rows and gaps go in `~/.claude/docs/dogfood.md`, as the repo `CLAUDE.md` and the global rules say (not asked; both rules name it).
4. PR flow: one PR per unit, merged into `wip` without asking, as the repo `CLAUDE.md` says for an approved plan (not asked; the rule decides it).

## As built (differences from this plan)

- `ITranslator` is `Task<string> CompleteAsync(systemPrompt, userPrompt, ct)`; the service builds the prompts and parses and validates the reply (the plan had the translator return a dictionary).
- `localization_translate` plan reply lists `{file, key, source}` per key with one top-level `prompt`, not a prompt per key. The `SamplingNotSupported` message lives in MCP Core's resx because the helper's `Strings` is internal.
- `StringScanner.InBuildFolder` stayed `internal`. `localization_pseudo` takes `root`, `resx`, `output`, `confirm`; `Strings.qps-ploc.resx` is git-ignored.
- Verified SDK members (ModelContextProtocol 2.4.0): `McpServer.SampleAsync(CreateMessageRequestParams, CancellationToken)`, `McpServer.ClientCapabilities.Sampling`.
- Not verified: the new tools through the running MCP server (the installed server predates them); the full gate's VSIX build, Tpm tests and npm steps were not run, only `dotnet format`, and `dotnet test` of LocalizationHelper, LintHelper and MCP.Core.
