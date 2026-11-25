# Event filtering and action summaries: implementation plan, 2026-11-25

Status: draft, not approved. Decisions from the user:
- Filtering runs in the MCP server only. The bridge keeps sending every event.
- `ide_build` and `ide_run_tests` wait for the result and return a summary of that action (status, counts, errors), not the event stream.
- Default event scope: run results, status and errors. Document saves and opens are off unless asked for.

## 1. Problem

Every event goes to the model through `ide_read_events`; a build can emit dozens, and a polling model spends tokens on routine
traffic. `ide_build` and `ide_run_tests` also return before the run is summarised, so the model polls and the chat can idle.

## 2. Goals

1. The model reads only the events it needs: filter by name, severity and sequence number.
2. `ide_build` and `ide_run_tests` return one capped summary when the run finishes.
3. A call never leaves the chat waiting without a reply: bounded wait, then a `running` reply.
4. Event payloads and summaries are capped.

Out of scope: changing what the bridge sends, the events pipe, or adding a bridge `testDone` event.

## 3. What the code does today (read before implementing)

- `EventSubscriptions` (`src/MCP/Core/src/Services/EventSubscriptions.cs`, public static): one pipe subscription per VS pid,
  subscribed with `{"action":"subscribe","args":{}}` (all events). Buffer: `List<(long Seq, string Name, string Json)>`, max
  `BufferLimit = 500`, oldest dropped. Each `Json` line is `{"event":name,"seq":n,"data":...}`. Heartbeats are not buffered but
  consume sequence numbers, so gaps in `seq` are normal and do not mean loss. `Read(int pid, long sinceSeq, IReadOnlyCollection<string>? names)`
  returns `{ pid, events, last_seq, closed, close_reason }` with no cap. `last_seq` is `0` when the buffer is empty.
- `EventAutoSubscribeService` (`BackgroundService`, 5 s loop): calls `EnsureSubscribedAsync` for every running VisualStudio instance.
  It needs no change; it subscribes to the full stream, which the build/test waiters rely on.
- `IdeTools.IdeReadEvents(int? instancePid, long sinceSeq, string[]? events)` is a sync tool; `null` events means all events.
  `IdeSubscribeEvents` finds the pid with a `health` call and reads `GetProperty("pid")` from the top level of the router reply.
  The router reply (`IdeInstanceRouter.SendAsync`) has `ide, instance_pid, solution_path, ok, result, error` at the top level, so
  new code reads `instance_pid` (verify `IdeSubscribeEvents` against a live instance; if it throws, fix it in unit 5, one line).
- Bridge events (`BridgeHost.cs`, data is all strings): `solutionOpened {solution}`, `solutionClosing` (null data),
  `buildBegin {scope, action}`, `buildDone {scope, action}` (no success flag, no counts), `enterBreakMode {reason}`,
  `enterRunMode {reason}`, `contextChanged {process, function}`, `documentSaved|documentOpened|documentClosing {file}`.
  `dropped {count}` is made by `EventHub` per subscriber when its 500-event queue overflows. Values are `Enum.ToString()` of the
  EnvDTE enums, e.g. `vsBuildActionBuild`, `vsBuildScopeSolution`, `dbgEventReasonExceptionNotHandled`.
- Bridge `build` action (`IdeActions.Build`): `SolutionBuild.Build(WaitForBuildToFinish: true)`; replies
  `{ succeeded, failed_projects }` when the build ends. So the reply already is the completion signal. But
  `IdeInstanceRouter.ReplyTimeout` is 60 s: a longer build makes `CallAsync` return `{ ok:false, busy:true, error }` while the build
  keeps running in VS.
- Bridge `getBuildDiagnostics` reply (through the router): `result: { count, diagnostics: [{ severity: "error"|"warning", file, line, column, message }] }`.
  There is no `code` field; the Error List description is the message only. Duplicates occur (one per target framework).
- Bridge `runTests` (`IdeActions.RunTests`) only dispatches `TestExplorer.RunAllTests` and replies at once with
  `{ command, refused, hresult }`. There is no test-finished event. `IdeTools.RunTestExplorerWithRetryAsync` retries once after 3 s on `refused`.
- `IdeGetTestSummary` calls `searchOutput` with pane `Tests` and pattern `Test run finished: \d+ Tests|\[FAIL\]|\[xUnit\.net`, then
  `TestSummaryParser.ParseLatest(raw)`. Parser output (ok): `{ ok, source, total, passed, failed, skipped, failures: [name], failure_details: [{name, message}], line }`.
  Not ok: `{ ok:false, running:true|false, error }`. The pattern does not match `Starting:` or `VSTest Adapter` lines, so the parser's
  `running` flag cannot fire through this call today (unit 6 fixes the pattern).
- `IdeRunTests` has three paths: `project` set (dotnet test), `IdeTestRunMode == DotnetTest` (dotnet test + TRX), else Test Explorer.
  The two dotnet paths already block until `dotnet test` ends and return their own reply.
- Tests: tool types are `internal`; `src/MCP/Server/tests` only holds a smoke test, so all new tests go in
  `src/MCP/Core/tests` (`Services/` for pure code, `Tools/` for tool calls), namespace `AiDevHelpers.MCP.Core.Tests.*`, xUnit, same
  style as `TestSummaryParserTests` and `IdeToolsCallTests` (`new IdeTools(Options.Create(new IdeToolsOptions()))`).
- User-facing strings go through `Strings.Get/Format` (`src/MCP/Core/src/Resources/Strings.resx`); `HardCodedStringTests` fails otherwise.
- Docs that must change: `docs/ide-bridge.md` (checked by `DocsMatchCodeTests`), `src/Common/src/Resources/mcp-tools.md`,
  `src/Common/src/Resources/mcp-self-test.md`.

## 4. Design

### 4.1 Types (new, `src/MCP/Core/src/Services/`)

```csharp
internal enum EventSeverity { Info = 0, Warning = 1, Error = 2 }

internal readonly record struct BufferedEvent(long Seq, string Name, string Json);

internal sealed record EventFilterOptions(
    IReadOnlyCollection<string>? Names,          // null or empty = every name
    EventSeverity MinSeverity = EventSeverity.Info,
    int Limit = 20,                              // clamped 1..200
    int MaxTextChars = 300);                     // clamped 20..2000

internal sealed record EventFilterResult(
    IReadOnlyList<JsonNode> Events,              // each {event, seq, data[, truncated:true]}
    int Omitted,                                 // matched but not returned because of Limit
    long NextSeq);                               // seq of the last returned event; last buffered seq when Omitted == 0
```

### 4.2 Severity mapping

`EventSeverityMap.Of(string name, JsonElement data)` is a pure function. Unknown names are `Info`.

| Event | Condition | Severity |
|---|---|---|
| `dropped` | always | Warning |
| `enterBreakMode` | `data.reason == "dbgEventReasonExceptionNotHandled"` | Error |
| `enterBreakMode` | `data.reason == "dbgEventReasonExceptionThrown"` | Warning |
| `enterBreakMode` | any other reason (breakpoint, step, user break) | Info |
| `buildDone` | always (the event carries no result; failure comes from the `ide_build` summary) | Info |
| `buildBegin`, `enterRunMode`, `contextChanged` | always | Info |
| `solutionOpened`, `solutionClosing` | always | Info |
| `documentSaved`, `documentOpened`, `documentClosing` | always | Info |
| any other name | always | Info |

This replaces the draft's "`buildDone` with failures is error": the bridge event has no failure data and the bridge stays unchanged.

### 4.3 Default scope

`ide_read_events` with `events` omitted uses `EventScope.DefaultNames` = `buildDone`, `enterBreakMode`, `enterRunMode`,
`solutionOpened`, `solutionClosing`, `dropped`, and `minSeverity` Info. `contextChanged` is left out (fires on every step).
`events: []` or `events: ["*"]` means every name. Any other list is used as given (same as today). This is a default change, not a compatibility shim.
`EventAutoSubscribeService` is unchanged.

### 4.4 Read reply shape

```json
{ "pid": 1234, "events": [ { "event": "buildDone", "seq": 41, "data": { "scope": "vsBuildScopeSolution", "action": "vsBuildActionBuild" } } ],
  "omitted": 0, "next_seq": 41, "last_seq": 41, "closed": false, "close_reason": null }
```
`omitted` counts matching events cut by `limit` (the oldest `limit` after `sinceSeq` are returned, so the caller continues with
`sinceSeq = next_seq`). Strings in `data` longer than `maxTextChars` are cut to that length plus `…`, and the event gets `"truncated": true`.
Invalid `minSeverity` returns `{ ok:false, error }` (string `InvalidMinSeverity`).

### 4.5 Wait behaviour (shared constants in `RunWaitDefaults`)

| Constant | Value |
|---|---|
| `DefaultWaitSeconds` | 120 (tool parameter `waitSeconds`, clamped 1..600) |
| `BuildPoll` | 500 ms |
| `TestPoll` | 2 s |
| `TestStartGrace` | 30 s (cap `waitSeconds`) |
| `MaxErrors`, `MaxFailures` | 20 |
| `MessageChars` | 200 (first line of a message) |
| `MaxReplyChars` | 6000 |

Common first steps (helper `IdeTools.ResolveRunTargetAsync(solutionPath, ide, instancePid, ct)`):
1. `health` through the router. Not ok: return that reply unchanged (keeps the "no instance" errors that existing tests assert).
2. `pid = instance_pid`, then `EventSubscriptions.EnsureSubscribedAsync(pid, ct)`. If it throws, continue without events (`events_available = false`):
   build waits on the call reply only, tests are unaffected (they poll the pane).
3. `since_seq = EventSubscriptions.LastSeq(pid)` taken after subscribing and before the action is sent. Buffer-wrap cannot hide the
   new events because only 500 are kept and `since_seq` is compared by value.

**Build** (`BuildRunWaiter`):
1. Start `call("build")` as a task (not awaited). Poll every `BuildPoll` until the deadline (`waitSeconds`).
2. Call task finished: parse it. `ok:true` gives `succeeded` and `failed_projects` directly; finish. `ok:false` with `busy:true` (router
   60 s timeout) means the build is still running: keep polling events. Any other `ok:false`: return that reply unchanged.
3. Events (only when `events_available`): `begin` = first `buildBegin` with `seq > since_seq` and `data.action == "vsBuildActionBuild"`.
   `done` = first `buildDone` with `seq > begin.seq` and the same `scope` and `action`. When `done` is found and the call reply was
   lost to the router timeout, `succeeded = (error count from diagnostics == 0)` and `failed_projects = null`.
4. Finished: read `ide_get_build_diagnostics` (`severity: "all"`) once and build the summary (4.6).
5. Deadline reached: reply `running` (below). The build keeps going in VS; a later `ide_read_events` shows `buildDone`.
6. `ct` cancelled: throw `OperationCanceledException`; the build is not stopped.
7. A build started by someone else during the wait cannot be told apart from ours when both are `vsBuildActionBuild`; the first one after `since_seq` wins. Accepted.

**Tests** (Test Explorer mode only; `project` and DotnetTest paths keep their reply):
1. `baseline = TestSummaryParser.Inspect(searchOutput).Summaries` before sending `runTests`. `Inspect` is new (below).
2. Send `RunTestExplorerWithRetryAsync`. If the reply is not `ok:true` or carries `attempts`/`error`, return it unchanged.
3. Poll every `TestPoll`: `searchOutput` pane `Tests` with `TestSummaryParser.SearchPattern`, then `Inspect`.
   - `Readable == false` (reply `ok:false`): return `{ ok:false, error }` at once (`CouldNotReadTestsPane`).
   - Finished when `Summaries >= 1` and `Summaries != baseline`. Then build the summary from `ParseLatest` (4.6).
   - Started when `RunInProgress` was seen in any poll or `Summaries != baseline`.
4. Pane text not recognised: if `Started` is still false after `TestStartGrace`, return
   `{ ok:false, status:"unrecognised", error:<TestsPaneNotRecognised>, hint:"Check Test Explorer, then call ide_get_test_results." }` without waiting for the full limit.
5. Deadline reached with `Started == true`: `running` reply.

`Inspect(string searchOutputJson)` returns `record TestPaneState(bool Readable, int Summaries, bool RunInProgress)`. `Summaries` counts
lines matching the existing `Test run finished: N Tests (...)` regex. `RunInProgress` is true when a line containing `Starting:` or
`VSTest Adapter` appears after the last summary line (or any such line when there is no summary). `TestSummaryParser.SearchPattern`
= the current pattern plus `|Starting:|VSTest Adapter`, shared by `IdeGetTestSummary` and the waiter.

`running` reply (both tools):
```json
{ "ok": true, "status": "running", "ide": "VisualStudio", "instance_pid": 1234, "solution_path": "...", "since_seq": 40,
  "waited_seconds": 120, "hint": "Call ide_read_events with sinceSeq=40, or ide_get_test_results." }
```
For tests, `hint` names `ide_get_test_results` only (no event marks test end).

### 4.6 Summary shapes

Build, finished:
```json
{ "ok": true, "status": "finished", "ide": "VisualStudio", "instance_pid": 1234, "solution_path": "...", "since_seq": 40,
  "succeeded": false, "failed_projects": 1, "error_count": 3, "errors": [ { "file": "A.cs", "line": 12, "column": 5, "code": "CS1002", "message": "; expected" } ],
  "errors_omitted": 0, "warning_count": 7, "duration_seconds": 14.2 }
```
`BuildSummary.FromDiagnostics(...)` produces it from the `getBuildDiagnostics` reply:
- Input path: `result.diagnostics[]` with fields `severity`, `file`, `line`, `column`, `message`.
- `errors` = items with `severity == "error"`, de-duplicated on `(file, line, column, message)`, in list order, first `MaxErrors`.
  `error_count` is the de-duplicated total; `errors_omitted = error_count - errors.Count`.
- `code` = first match of `\b[A-Z]{2,6}\d{3,5}\b` in the message, else `null` (the Error List gives no separate code).
- `message` = first line, cut to `MessageChars`. `file` is kept as the IDE gives it.
- `warning_count` = count of `severity == "warning"` (de-duplicated the same way).
- `succeeded` and `failed_projects` come from the build reply; see 4.5 step 3 when only the event is known.
- If the diagnostics call is not ok: the summary has `errors: []`, `error_count: null`, and `diagnostics_error: <error>`.
- `duration_seconds` = wall time from sending the build to finish, one decimal.

Tests, finished:
```json
{ "ok": true, "status": "finished", "ide": "...", "instance_pid": 1234, "solution_path": "...",
  "total": 120, "passed": 118, "failed": 1, "skipped": 1,
  "failing_tests": ["NS.Class.Name"], "failure_messages": ["Assert.Equal() Failure"], "failures_omitted": 0 }
```
From `ParseLatest`: `failing_tests` = `failures` (first `MaxFailures`), `failure_messages` = first line of each `failure_details[].message`
(same index, cut to `MessageChars`, empty string when none), `failures_omitted = failed_names_total - failing_tests.Count`.
`failed` can exceed `failing_tests.Count` when the pane lists no `[FAIL]` lines; that is not an error.

Reply size guard: if the serialized reply exceeds `MaxReplyChars`, halve `errors` / `failing_tests` + `failure_messages` until it fits and
add the cut to `*_omitted`.

## 5. Work units

Each unit is one commit with its own tests. Before each commit run the lite gate (`.claude/reference/lite-gate.md`); use the
aidevtools tools for build and tests (`ide_build`, `ide_run_tests`, `ide_get_test_results`). Commit message is the subject line given.

### Unit 1. Filter function

Files:
- new `src/MCP/Core/src/Services/EventFilter.cs`
- new `src/MCP/Core/tests/Services/EventFilterTests.cs`

Signatures:
```csharp
internal static class EventFilter
{
    public static EventFilterResult Apply(IEnumerable<BufferedEvent> buffer, long sinceSeq, EventFilterOptions options);
}
```
`Apply` keeps buffer order, drops `Seq <= sinceSeq`, applies names, then `MinSeverity` via `EventSeverityMap.Of`
(until unit 2 lands, a stub that returns `Info` for every event; unit 2 replaces it), then `Limit`, then the text cap
(`JsonNode` walk over `data`; marks `truncated`). Types from 4.1 live in `EventFilter.cs`. Clamping happens in `Apply`.

Tests (`EventFilterTests`):
- `Apply_NoNames_ReturnsAllAfterSinceSeq`: three events, `sinceSeq` 1 returns seq 2 and 3.
- `Apply_Names_KeepsOnlyListed`: names `[buildDone]` drops the rest.
- `Apply_EmptyNames_MeansAll`: empty list behaves like null.
- `Apply_Limit_ReturnsOldestAndCountsOmitted`: 5 events, limit 2: seq 1,2 returned, `Omitted == 3`, `NextSeq == 2`.
- `Apply_NoOverflow_NextSeqIsLastBuffered`: `NextSeq` equals the last buffer seq, `Omitted == 0`.
- `Apply_LimitClamped`: limit 0 acts as 1; limit 9999 acts as 200.
- `Apply_LongString_TruncatedWithMarker`: 1000-char `data.file` becomes `maxTextChars` + `…` and the event has `truncated: true`.
- `Apply_ShortStrings_NotMarked`: no `truncated` property.
- `Apply_NestedLongString_Truncated`: a long string in a nested object is cut.
- `Apply_NullData_Passes`: `solutionClosing` with `data: null` is returned intact.
- `Apply_EmptyBuffer_ReturnsEmpty`: `NextSeq == sinceSeq`.

Commit: `MCP: add a pure event filter with limit and text caps`.
Done: new tests pass; whole `AiDevHelpers.MCP.Core.Tests` project green; lite gate passes.

### Unit 2. Severity mapping

Files:
- new `src/MCP/Core/src/Services/EventSeverityMap.cs`
- edit `EventFilter.cs` (replace the stub)
- new `src/MCP/Core/tests/Services/EventSeverityMapTests.cs`
- add cases to `EventFilterTests.cs`

Signatures:
```csharp
internal static class EventSeverityMap
{
    public static EventSeverity Of(string name, JsonElement data);          // table in 4.2
    public static bool TryParse(string? text, out EventSeverity severity);  // "info|warning|error", case-insensitive; null maps to Info
}
```

Tests (`EventSeverityMapTests`):
- `Dropped_IsWarning`.
- `EnterBreakMode_ExceptionNotHandled_IsError`.
- `EnterBreakMode_ExceptionThrown_IsWarning`.
- `EnterBreakMode_Breakpoint_IsInfo` and `_Step_IsInfo` (theory over reasons).
- `EnterBreakMode_NoReasonProperty_IsInfo`.
- `BuildDone_IsInfo`.
- `Theory_OtherKnownEvents_AreInfo` for every other name in the table.
- `UnknownEvent_IsInfo`.
- `TryParse_AcceptsThreeNamesAnyCase`, `TryParse_Null_IsInfo`, `TryParse_Garbage_ReturnsFalse`.
Added to `EventFilterTests`: `Apply_MinWarning_DropsInfo`, `Apply_MinError_KeepsOnlyErrors`, `Apply_MinSeverityAppliesBeforeLimit` (limit counts only passing events).

Commit: `MCP: map event names to severity`.
Done: tests green; lite gate passes.

### Unit 3. `ide_read_events` options

Files:
- edit `src/MCP/Core/src/Services/EventSubscriptions.cs`
- edit `src/MCP/Core/src/IdeTools.cs` (`IdeReadEvents`)
- edit `src/MCP/Core/src/Resources/Strings.resx` (+ designer/`Strings` accessors as the repo does): `InvalidMinSeverity`
- new `src/MCP/Core/tests/Tools/IdeReadEventsToolTests.cs`
- new `src/MCP/Core/tests/Services/EventSubscriptionsReadTests.cs`

Signatures:
```csharp
// EventSubscriptions
public static object Read(int pid, long sinceSeq, EventFilterOptions options);   // replaces the names overload; update every caller (grep first)
internal static long LastSeq(int pid);                                            // 0 when empty; KeyNotFoundException when not subscribed
internal static IReadOnlyList<BufferedEvent> Since(int pid, long sinceSeq);       // unfiltered copy under the subscription lock
internal static void AddForTest(int pid, long seq, string name, string json);     // creates an unconnected subscription; test seam only
internal static void ResetForTest();

// IdeTools
public string IdeReadEvents(
    int? instancePid = null, long sinceSeq = 0, string[]? events = null,
    string? minSeverity = null, int limit = 20, int maxTextChars = 300);
```
`Read` returns the 4.4 shape (anonymous object adding `omitted`, `next_seq`). In this unit `events == null` still means all names
(the default scope arrives in unit 4). Update the `[Description]` of the tool to list the new parameters and the severity values.

Tests:
- `EventSubscriptionsReadTests.Read_ReturnsOmittedAndNextSeq`: seeded 30 events, default limit: 20 returned, `omitted` 10, `next_seq` 20.
- `Read_LastSeqIsBufferTail`: `last_seq` unaffected by the limit.
- `Since_ReturnsAllNamesUnfiltered`.
- `LastSeq_EmptyBuffer_IsZero`.
- `Read_UnknownPid_Throws` (`KeyNotFoundException`, same as today).
- `IdeReadEventsToolTests.IdeReadEvents_MinSeverityInvalid_ReportsError`.
- `IdeReadEvents_MinSeverityWarning_ReturnsDropped`.
- `IdeReadEvents_EventsNamesStillWork`.
- `IdeReadEvents_MaxTextChars_Truncates`.
Tests call `ResetForTest()` in the constructor (static state).

Commit: `MCP: ide_read_events takes minSeverity, limit and maxTextChars`.
Done: tests green; `ide_read_events` against a live VS returns the new fields (`omitted`, `next_seq`); lite gate passes.

### Unit 4. Default scope

Files:
- edit `IdeTools.cs` (`IdeReadEvents`), `EventSubscriptions.cs` (or `EventScope` class in `EventFilter.cs`)
- edit `IdeReadEventsToolTests.cs`

Signatures:
```csharp
internal static class EventScope
{
    public static readonly IReadOnlyList<string> DefaultNames =
        ["buildDone", "enterBreakMode", "enterRunMode", "solutionOpened", "solutionClosing", "dropped"];
    public static IReadOnlyCollection<string>? Resolve(string[]? requested); // null -> DefaultNames; [] or contains "*" -> null (all); else requested
}
```
The tool description states the default and the `["*"]` escape.

Tests:
- `Resolve_Null_ReturnsDefaults`; `Resolve_Empty_ReturnsAll`; `Resolve_Star_ReturnsAll`; `Resolve_Names_ReturnsGiven`.
- `IdeReadEvents_Default_HidesDocumentAndBuildBegin`: seeded buildBegin, documentSaved, contextChanged, buildDone: only buildDone returned.
- `IdeReadEvents_DefaultKeepsDropped`.
- `IdeReadEvents_StarReturnsEverything`.
- `IdeReadEvents_ExplicitDocumentSaved_ReturnsIt`.

Commit: `MCP: ide_read_events defaults to run results, status and errors`.
Done: tests green; live check: after a build in VS the default read shows `buildDone` and no `documentSaved`; lite gate passes.

### Unit 5. `ide_build` summary

Files:
- new `src/MCP/Core/src/Services/BuildRunWaiter.cs` (waiter, 4.5)
- new `src/MCP/Core/src/Services/BuildSummary.cs` (4.6)
- new `src/MCP/Core/src/Services/RunWaitDefaults.cs`
- edit `IdeTools.cs`: `IdeBuild`, helper `ResolveRunTargetAsync`
- edit `Strings.resx`: none for build beyond a `BuildWaitHint` string
- new `src/MCP/Core/tests/Services/BuildRunWaiterTests.cs`, `BuildSummaryTests.cs`
- add `IdeBuild_NoInstance_ReportsError` is already in `IdeToolsCallTests` if present; keep passing

Signatures:
```csharp
// IdeTools
public Task<string> IdeBuild(string? solutionPath = null, string? ide = null, int? instancePid = null,
    int waitSeconds = 120, CancellationToken ct = default);

internal sealed class BuildRunWaiter(
    Func<string, object, CancellationToken, Task<string>> call,          // router call bound to solution/ide/pid
    Func<long, IReadOnlyList<BufferedEvent>> eventsSince,                // null when events are not available
    Func<TimeSpan, CancellationToken, Task> delay,
    Func<DateTime> utcNow)
{
    public Task<string> RunAsync(long sinceSeq, bool eventsAvailable, int waitSeconds, string routerHeaderJson, CancellationToken ct);
}

internal static class BuildSummary
{
    public static string Finished(string diagnosticsReplyJson, bool? succeeded, int? failedProjects,
        double durationSeconds, long sinceSeq, string routerHeaderJson);
    public static string Running(long sinceSeq, int waitedSeconds, string routerHeaderJson);
}
```
`routerHeaderJson` carries `ide`, `instance_pid`, `solution_path` from the health reply so every reply names the instance.
Tool description: says it waits up to `waitSeconds` (default 120) and returns the summary or `status:"running"`; errors beyond 20 are counted in `errors_omitted`.

Tests (waiter uses fake `call`, fake clock and delay that advances the clock):
- `Run_BuildReplyOk_ReturnsSucceededTrue_NoDiagnosticsErrors`.
- `Run_BuildReplyFailed_ListsErrorsFromDiagnostics`: `succeeded:false, failed_projects:1`.
- `Run_ReplyNotOk_NotBusy_ReturnsReplyUnchanged`.
- `Run_ReplyBusy_WaitsForBuildDoneEvent`: call returns `busy:true`, then events `buildBegin(41)`, `buildDone(44)`: finished, `succeeded` from diagnostics, `failed_projects` null.
- `Run_BuildDoneBeforeSince_IsIgnored`: a `buildDone` with `seq <= since` does not finish the wait.
- `Run_BuildDoneOtherScope_IsIgnored`: begin scope Solution, done scope Project: not matched.
- `Run_BuildBeginOtherAction_IsIgnored`: `vsBuildActionClean` begin does not start tracking.
- `Run_NeverCompletes_ReturnsRunningAfterWait`: `status:"running"`, `since_seq` equal, `waited_seconds` equal to the limit.
- `Run_EventsUnavailable_Busy_ReturnsRunning`.
- `Run_Cancelled_Throws`.
- `BuildSummaryTests`: `Finished_CapsErrorsAt20AndCountsOmitted` (25 errors), `_DeduplicatesAcrossTargetFrameworks`, `_ExtractsCodeFromMessage` (`CS1002` found; message without code gives `null`),
  `_FirstLineOnlyAndTruncated`, `_WarningsCounted`, `_DiagnosticsNotOk_HasDiagnosticsError`, `_ReplyOverSizeGuard_Halves`, `_SucceededBuild_NoErrors`.
- Tool: `IdeBuild_NoInstance_ReportsError` (existing style, `AssertNotOk`).
- Tool: `IdeBuild_WaitSecondsClamped` via a pure `RunWaitDefaults.ClampWait(int)` test (0 -> 1, 9999 -> 600).

Commit: `MCP: ide_build waits and returns a capped summary`.
Done: tests green; live checks in section 6 (compile error, success, 5-second limit); lite gate passes.

### Unit 6. `ide_run_tests` summary

Files:
- edit `src/MCP/Core/src/TestSummaryParser.cs`: `SearchPattern`, `Inspect`, `TestPaneState`
- edit `IdeTools.cs`: `IdeGetTestSummary` (use `SearchPattern`), `IdeRunTests`
- new `src/MCP/Core/src/Services/TestRunWaiter.cs`, `TestRunSummary.cs`
- edit `Strings.resx`: `TestsPaneNotRecognised`
- new `src/MCP/Core/tests/Services/TestRunWaiterTests.cs`, `TestRunSummaryTests.cs`; extend `TestSummaryParserTests.cs`

Signatures:
```csharp
// TestSummaryParser
internal const string SearchPattern = @"Test run finished: \d+ Tests|\[FAIL\]|\[xUnit\.net|Starting:|VSTest Adapter";
internal sealed record TestPaneState(bool Readable, int Summaries, bool RunInProgress);
public static TestPaneState Inspect(string searchOutputJson);

// IdeTools
public Task<string> IdeRunTests(string? solutionPath = null, string? ide = null, int? instancePid = null,
    string? project = null, int waitSeconds = 120, CancellationToken ct = default);

internal sealed class TestRunWaiter(
    Func<CancellationToken, Task<string>> readPane,        // searchOutput with SearchPattern
    Func<CancellationToken, Task<string>> startRun,        // RunTestExplorerWithRetryAsync
    Func<TimeSpan, CancellationToken, Task> delay,
    Func<DateTime> utcNow)
{
    public Task<string> RunAsync(int waitSeconds, string routerHeaderJson, long sinceSeq, CancellationToken ct);
}

internal static class TestRunSummary
{
    public static string Finished(string parseLatestJson, string routerHeaderJson);
    public static string Running(long sinceSeq, int waitedSeconds, string routerHeaderJson);
    public static string Unrecognised(string routerHeaderJson);
}
```
The `project` and DotnetTest paths are not changed (they already block); only the Test Explorer path waits. `waitSeconds` is ignored on those paths and the description says so.

Tests:
- `TestSummaryParserTests` additions: `Inspect_NoSummary_CountsZero`; `Inspect_TwoSummaries_CountsTwo`; `Inspect_StartingAfterLastSummary_RunInProgress`; `Inspect_StartingBeforeLastSummary_NotInProgress`; `Inspect_PaneError_NotReadable`; `Inspect_AdapterLineOnly_RunInProgress`.
- `TestRunWaiterTests`:
  - `Run_NewSummaryAppears_ReturnsFinished`: baseline 1, then 2.
  - `Run_FirstEverRun_BaselineZero_SummaryOne_Finishes`.
  - `Run_OldSummaryOnly_DoesNotFinish`: count stays at baseline, run in progress: `running` at deadline.
  - `Run_RefusedTwice_ReturnsRefusalUnchanged`: `startRun` returns the existing refused-twice error, no polling.
  - `Run_PaneUnreadable_ReturnsErrorAtOnce`.
  - `Run_NothingStarts_ReturnsUnrecognisedAfterGrace`: no Starting lines, count unchanged, returns at 30 s, not at the wait limit.
  - `Run_RunInProgressThenDeadline_ReturnsRunning`.
  - `Run_Cancelled_Throws`.
- `TestRunSummaryTests`: `Finished_MapsCounts`, `_CapsFailuresAt20`, `_MessagesAreFirstLine`, `_FailuresFewerThanFailed_NotAnError`, `_MissingMessage_EmptyString`, `_SizeGuardHalves`.
- Tool: `IdeRunTests_NoInstance_ReportsError` (existing test must keep passing).

Commit: `MCP: ide_run_tests waits for the Tests pane and returns a capped summary`.
Done: tests green; live check with one failing test and a 5-second limit; lite gate passes.

### Unit 7. Docs and self-test

Files:
- edit `docs/ide-bridge.md` (tools: new parameters, default scope, reply shapes, severity table)
- edit `src/Common/src/Resources/mcp-tools.md`
- edit `src/Common/src/Resources/mcp-self-test.md`
- no change to `conformance/protocol.md` (the bridge and pipe are unchanged); add one line there only if the doc test requires it

Content:
- `ide_read_events`: parameters, default names, `["*"]`, `omitted`/`next_seq`, severity table.
- `ide_build`, `ide_run_tests`: `waitSeconds`, finished and `running` shapes, "Test Explorer mode only waits".
- Self-test section 2 (build and tests): the build check expects `status:"finished"` with `errors` capped at 20; the test check expects the summary. Section 4 (read-only): the existing `ide_read_events` line expects the default scope; add a check of `minSeverity: "warning"`. Section 5 (action checks): events line now asks for `events: ["*"]` to see `buildBegin` and `documentSaved`. Add the token check (old vs new reply size, recorded in the report).

Tests: existing `DocsMatchCodeTests` and `SelfTestCoverageTests` pass; no new tools are added so no new tool names are required.
Commit: `Docs: describe event filtering and run summaries`.
Done: `DocsMatchCodeTests`, `SelfTestCoverageTests`, `HardCodedStringTests` pass; lite gate passes. Before the PR to `dev` run the full gate.

## 6. Verification (live, Visual Studio, scratch solution)

- Build with one compile error: `succeeded:false`, one error with file, line, `code`.
- Build that succeeds: `succeeded:true`, `errors: []`.
- Long build with `waitSeconds: 5`: `status:"running"`; `ide_read_events` with `sinceSeq` later shows `buildDone`.
- Build longer than 60 s: finished through the `buildDone` path (`failed_projects: null`).
- Tests with one failing test: the summary lists it; with the Tests pane cleared first: the summary still appears.
- Default `ide_read_events` after saving a file: no `documentSaved`; with `events:["documentSaved"]`: present.
- Token check: same build old vs new, reply sizes recorded in the self-test report.

## 7. Risks

- Test completion depends on the Tests pane text (`Test run finished: N Tests (...)`, `Starting:`, `VSTest Adapter`). A different VS version can change it; the `unrecognised` reply and the self-test check make that visible.
- A held call: bounded by `waitSeconds` (max 600). The router's own 60 s reply timeout is why builds longer than that use the event path.
- Filters in the MCP server do not shrink pipe traffic; the pipe is local, accepted.
- `buildDone` has no failure flag, so event severity cannot mark failed builds; the `ide_build` summary does.
- Test-run completion by count change fails if the pane is cleared and refilled to the same count (baseline 1 and result 1). Accepted, see open items.

## 8. Alternatives considered

1. Filter in the bridge instead of the MCP server. The pipe already supports `{"events":[...]}` at subscribe (`EventSubscriber` names),
   which would cut pipe bytes. Kept MCP-side: the auto-subscription must see every event so the build waiter can match `buildBegin`/`buildDone`
   and different callers want different views of one buffer; severity and caps are per read; the pipe is local so traffic is cheap. The bridge stays untouched.
2. Build completion. The draft waited on `buildDone`. The bridge `build` action already blocks (`WaitForBuildToFinish: true`) and replies with
   `succeeded`, so the reply is the primary signal; `buildDone` is only the fallback after the router's 60 s timeout. Changed from the draft: simpler and exact for builds under 60 s.
   Rejected: raising `ReplyTimeout` (affects every action), making the bridge action asynchronous (bridge change).
3. Test completion. No bridge event exists. Options: add a `testDone` event through the VS test service (bridge change, out of scope); write a TRX
   (Test Explorer mode writes none); switch the run to `DotnetTest` (already blocking, but a different run mode and not what the user has set);
   poll the Tests pane (chosen). Polling is one local pipe call every 2 s and costs no model tokens, since the waiting happens inside the tool.
   Baseline count of summary lines is used instead of clearing the pane, which would erase history the user may read.
4. MCP progress notifications to keep the client alive. Rejected: client support varies and the bounded wait with a `running` reply already covers it.
5. Default scope. Kept the user's scope (run results, status, errors). Added `dropped` and exception breaks via severity. `contextChanged` is off by default
   because it fires on every step; `buildBegin` is off because the summary replaces it.
6. Token caps. Per-item caps (20 items, 300 chars, first line) plus a total size guard (`MaxReplyChars` 6000) on summaries, because the cost is dominated by
   error and failure messages, not by event envelopes. A single global character budget was considered for events too; rejected as harder to predict than `limit`.

## 9. Decisions and open items

Decided by the user:
- Exception breaks (4.2) keep Warning/Error severity, so they are in the default scope.
- Test summaries do not clear the Tests pane before a run. A finished run can be missed only if the refill matches the old count; the call then falls back to `ide_get_test_results`.
- The wait limit and caps are settings, not constants.

Settings (defaults in code, overridable):
- Location: `.claude/aidevhelpers.json` in the solution's repo, the file `RepoConfig` already reads for `preferredIde`. Add an `eventFilter` object:
  `{ "waitSeconds": 120, "maxEvents": 20, "maxEventTextChars": 300, "maxSummaryMessageChars": 200, "maxReplyChars": 6000 }`.
- Each field is optional. A missing or invalid value falls back to its default and the reply notes it in `settings_warnings`.
- A per-call argument (for example `waitSeconds` on `ide_build`) overrides the setting for that call.
- Add a unit to step 1 of the plan: read the `eventFilter` object in `RepoConfig` with tests for defaults, overrides and invalid values.

Still open:
- `IdeSubscribeEvents` reads `pid` at the top level of the router reply, which the router does not write. Verify live in unit 5; if broken, read `instance_pid` and note it in the commit.
