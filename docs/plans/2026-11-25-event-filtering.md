# Event filtering and action summaries: plan, 2026-11-25

Status: draft, not approved. Decisions taken from the user:
- Filtering runs in the MCP server only. The bridge keeps sending every event; the MCP server narrows what the model sees.
- A build or test call waits for its completion and returns a summary of that action only (status and errors), not the event stream.
- The automatic subscription keeps errors, status and run results by default. Document saves and opens are off unless asked for.

## 1. Problem

Every event goes to the calling model through `ide_read_events`. A build can produce dozens of events, and a model that polls
them spends tokens on routine traffic. A build or test call also returns to the chat before the run finishes, so the model has
to poll and the chat can go idle.

## 2. Goals

1. The model reads only the events it needs: filtered by name, severity, and sequence number.
2. `ide_build` and `ide_run_tests` return one summary when the run finishes: status, counts, and errors (capped).
3. A call never leaves the chat waiting without a reply: it has a maximum wait and returns a run id if the run is still going.
4. Event payloads are capped, so one large message cannot flood a reply.

Out of scope: changing what the bridge sends (it keeps the full stream), and changing the events pipe.

## 3. Design

### 3.1 Event filter (MCP server)

`EventSubscriptions.Read` already takes a name list. Extend it, in one place, with:

- `names`: event names (existing).
- `minSeverity`: `info | warning | error`. Severity comes from the event: `buildDone` with failures is error; `enterBreakMode` is info;
  a `dropped` event is warning.
- `sinceSeq`: sequence number (existing).
- `limit`: maximum events returned (default 20). The reply says how many were left out (`omitted`).
- `maxTextChars`: cap on each event's text (default 300).

The filter is a pure function over `(events, filter)`, so it is unit-tested in `src/MCP/Server/tests` without Visual Studio.

### 3.2 Default scope

`EventAutoSubscribeService` keeps subscribing to the full stream (the bridge sends everything). The default read filter is:

- `names`: `buildDone`, `enterBreakMode`, `enterRunMode`, `solutionOpened`, `solutionClosing`, `dropped`.
- `minSeverity`: `info`.

`buildBegin` and `document*` events are dropped by default. A caller can ask for them by name.

### 3.3 Build and test summaries

- `ide_build` (MCP): sends the `build` action, then waits for the matching `buildDone` event (sequence number after the build
  started). Returns:
  `{ succeeded, failed_projects, errors: [{file, line, code, message}] (max 20), warning_count, duration_seconds }`.
  Errors come from `ide_get_build_diagnostics` with severity `error`, so the summary is built from the same IDE data.
- `ide_run_tests` (MCP): sends `runTests`, then polls `ide_get_test_results` until the run is finished (test pane shows a
  final count, or the TRX is written), up to the wait limit. Returns `{ passed, failed, skipped, failing_tests: [name]
  (max 20), failure_messages: [first line each, max 20] }`.
  Test completion is read from the Tests pane. A dedicated `testDone` bridge event is not part of this plan.

### 3.4 Never leave the chat idle

- Each build or test call has a wait limit (default 120 seconds, configurable per call).
- If the run finishes in time, the reply is the summary, with `status: "finished"`.
- If it does not, the reply is `{ status: "running", since_seq, hint: "call ide_read_events with sinceSeq" }`. The caller gets an
  answer at once and can read progress without holding the chat.

### 3.5 Token caps

- `limit` (events) and `maxTextChars` (per event text) apply to every event read.
- Summaries are capped (errors 20, failing tests 20, first line of each message).
- A reply that is still too large is trimmed and says so in `omitted`.

## 4. Work units

Each unit is one commit with its own tests.

1. **Filter function and tests.** Pure filter (names, severity, limit, text cap, omitted count) in the MCP server, with unit tests.
2. **Severity mapping.** Map each event name to a severity (`buildDone` error when failed; `dropped` warning; others info).
   Unit tests for the mapping.
3. **`ide_read_events` options.** Add `minSeverity`, `limit`, `maxTextChars` and `omitted` to the reply. Keep the existing
   parameters working. Update the tool description.
4. **Default scope.** Set the default names and severity in `ide_read_events`. Document the default in the tool description.
5. **`ide_build` summary.** Wait for `buildDone` after the build started, then return the summary (section 3.3). Wait limit and
   `running` reply (section 3.4).
6. **`ide_run_tests` summary.** Poll `ide_get_test_results` until finished or the wait limit (section 3.3, 3.4).
7. **Docs and self-test.** Update `mcp-tools.md`, `ide-bridge.md`, and `mcp-self-test.md` (section 2: the build and test
   checks now expect a summary; section 3: the new read options).

## 5. Verification

- Unit tests: filter, severity mapping, summary builders (errors capped, failing tests capped, omitted count).
- Live checks in Visual Studio on a scratch solution:
  - Build with one compile error: the summary lists the error and `succeeded: false`.
  - Build that succeeds: `succeeded: true`, no errors.
  - Tests with one failing test: the summary lists it.
  - A long build with a 5-second wait limit: the reply is `running`, and `ide_read_events` with `sinceSeq` shows the rest.
- Token check: the same build with the old and new tools, and the size of each reply, recorded in the self-test.

## 6. Risks

- Test completion is read from the Tests pane. The pane text format can change between VS versions; the parser needs a self-test check.
- A wait inside an MCP call holds that call open. The wait limit keeps it bounded.
- Filters in the MCP server don't reduce the bridge pipe traffic. The pipe is local, so this is accepted.

## 7. Open items

None blocking. The default wait limit (120 seconds) and the caps (20 items, 300 characters) are defaults and can change in review.
