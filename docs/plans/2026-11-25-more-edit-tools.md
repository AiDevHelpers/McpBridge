# More C# edit tools: implementation plan, 2026-11-25

Status: revision 3, after the Sonnet review of revision 2 (section 7). Not started. Each unit is one commit and includes its own tests,
written first.

## 1. Scope

The C# edit tools that the Visual Studio bridge does not have yet. Already built (see `docs/ide-bridge.md`): `ide_edit_method`,
`ide_add_method`, `ide_add_overload`, `ide_add_member`, `ide_edit_member`, `ide_tdd_add_test`, `ide_tdd_update_test`,
`ide_rename_namespace`, `ide_move_type`, `ide_rename_symbol`, `ide_replace_in_files`, `ide_code_cleanup`,
`ide_semantic_references`, `ide_semantic_definition`.

Adds: safe delete, change signature with call-site rewrite, the non-method members, type creation and type-level edits, interface
and override generation, moving members and splitting files, extract and inline refactorings, conversions, code actions and
cleanup, and docs-only edits.

Out of scope: anything outside C# code (project files, NuGet, XAML, resx, JSON, VB, F#) and VS Code equivalents. Every tool here is
Visual Studio only and says so in its description.

## 2. Decisions

- Every tool is plan, then confirm: no `confirm` returns the checks and changes nothing.
- **Reference check on every member, any accessibility.** A change other code can feel is refused unless every reference is covered,
  by a `callerEdits` entry or, from phase 2, by the call-site rewriter. A delete is refused while any reference remains. Private
  members are checked too.
- Never invent text: doc comments, names and default values come from the caller. A missing one is a warning or a refusal.
- Engines are pure functions of a Roslyn `Solution`, tested on an `AdhocWorkspace`. The bridge applies the result with one
  `TryApplyChanges`.
- After an applied change the MCP server runs the tests that call the thing, with coverage (`MethodTestRunner`). Where no test
  calls it (a delete, a type edit), the test projects the changed files affect run whole, as for a method with no direct test.
- **Tool budget:** at most about 20 new MCP tools. Kinds of one operation share a tool with a `kind` or `target` argument
  (`ide_delete`, `ide_add_type`, `ide_convert`, `ide_generate_members`, `ide_extract`).
- Roslyn code actions are reached by the spike 0.0 before any bespoke engine is built for a refactoring Visual Studio already has.
  If the spike works, the code-action units replace the bespoke ones they cover; if not, the bespoke engines are built.
- Decided by the user: delete refuses and lists (no `deleteCallers`); a new required parameter needs `valueForCallers`; a new type's
  file goes beside the anchor file, with a warning, when folders do not follow namespaces; if 0.0 fails, all of 6.4 to 6.8 are
  built bespoke.

## 3. Cross-cutting contracts

- **Reply shape** is the built one: `{planned, applied, check:{refused, reasons, files, warnings, new_errors, new_warnings,
  new_notices, tests, method, ...}}`. New fields are added, none renamed.
- **Format and diagnostics:** the anchor file is formatted with its own line ending; diagnostics new since the change are
  reported and errors refuse it. Tools that can touch many files do not format.
- **Plan hash:** a plan returns `plan_hash` (over the changed texts). `confirm` re-plans and refuses if the hash differs from the
  `planHash` argument, so the user approves exactly what is applied (0.5).
- **Documents:** one logical document is one file path. In a multi-targeted project or with linked files the same path is several
  `DocumentId`s; an edit is planned once per path and applied to each id (0.6). Source-generator output and read-only files are
  refused. Unsaved editor text is what the plan reads (Roslyn sees it); a closed file is changed in the workspace and saved by the
  bridge.
- **Partial types:** a member edit applies to the part that declares it; a type-level edit (4.2, 5.x) needs every part and plans
  them together.
- **Threading:** planning runs off the UI thread on a workspace snapshot, with a timeout and cancellation; only the apply runs on
  the UI thread (0.4).
- **Docs:** a doc comment follows its member (`MethodDocUpdater`), with text from the caller.
- **Strings** go in `Resources/Strings.resx`; literals that are not user text go in the hard-coded-strings allowlist with a reason.
- **Each unit** ends with: engine, bridge action, MCP tool, rows in `docs/ide-bridge.md` and `mcp-tools.md`, a self-test line, a
  conformance fixture, its tests, the lite gate, commit, push.
- **Each phase** ends with a live check in a scratch solution, when the user agrees to reinstall the extension (VS closed, one
  installer run). Results go to `docs/dogfood.md`.

## 4. Phases

### Phase 0: shared parts

- [x] **0.0** Spike result (2026-11-25, `Microsoft.CodeAnalysis.CSharp.Features.dll` and `Microsoft.CodeAnalysis.Features.dll` from the
      VS 18 Community install, read with the metadata reader; nothing loaded or run): every C# fix and refactoring provider there
      is internal (342 types match `Refactor|CodeFix`, none public). `ICodeFixService` and `ICodeRefactoringService` are internal
      in `Microsoft.CodeAnalysis.Features.dll`. Only the abstract `CodeFixProvider` and `CodeRefactoringProvider` are public. So the
      extension cannot reach Visual Studio's own refactorings. Decision per section 2: units 6.1 and 6.2 (list and apply code
      actions) are dropped; phase 6 is bespoke (6.4 to 6.8); 7.x is bespoke; 8.1 is covered by `ide_code_cleanup`. Original
      question: (was: spike, one day, first): can the net48 extension reach Roslyn code fixes and refactorings? `ICodeFixService` and
      `ICodeRefactoringService` are probably internal, so try the public `CodeFixProvider` and `CodeRefactoringProvider` MEF
      exports. Record what compiles and runs, whether a code action returns only text changes or other operations, and whether
      action ids are stable between calls. The result decides phase 6 and units 7.x and 8.1.
- [x] **0.1** (done: `TestSolutions.cs`, two tests; `MethodEditEngineTests.Create` now delegates to it; 75 VS Core tests pass. The other
      test files still build their own solutions and move in later units.) `TestSolutions`: one test helper for multi-file, multi-project and multi-target `AdhocWorkspace` solutions with
      framework stand-ins; the existing test files move onto it.
- [x] **0.2** (done: `EditTarget.cs` covers every member kind and takes the kinds each engine accepts; `MemberEditEngine` uses it with
      the property and field kinds, no behaviour change; `MethodEditEngine` still has its own locator because its column rule
      differs, and moves onto `EditTarget` in 3.1 when it accepts other kinds; 80 VS Core tests pass.) `EditTarget`: one locator for the document, the symbol and its references, for every member kind (method,
      constructor, operator, indexer, property, field, event, type, enum member). Replaces the copies in `MethodEditEngine` and
      `MemberEditEngine`. Built engines move onto it with no behaviour change.
- [x] **0.3** (done: `CoverageOfReferences.cs`; the member and method engines use it for uncovered references and related members.
      Behaviour change: a virtual or abstract property now also refuses a signature change that overrides it, as methods already
      did. 83 VS Core tests pass.) `CoverageOfReferences`: the "every reference covered, related members refused" rule for symbols of any member kind
      (methods and overrides, properties, fields, events, interface members). Namespace and type moves keep their qualifier logic;
      they do not use it.
- [ ] **0.4** Planning off the UI thread: `RunPlan` plans on a workspace snapshot with a timeout (default 60 s,
      `timeoutSeconds`) and a cancellation token, then applies on the UI thread. A search that does not finish refuses with
      `incomplete: true` (`SymbolFinder` gives no partial result). A test shows the UI thread stays free during a plan.
- [ ] **0.5** Plan hash (see section 3), in `RunPlan` and every tool; `planHash` argument on every tool.
- [ ] **0.6** One logical document: multi-target and linked files resolve a path to all its `DocumentId`s, de-duplicate references
      by path and position, edit once, and report the `#if` branches Roslyn did not see in each target. Generated and read-only
      documents are refused.
- [ ] **0.7** `diff` in every plan reply: a unified diff per changed file, capped at 40 files and 400 lines each.
- [ ] **0.8** `FileMover` creates and deletes files as well as moving them (SDK-style projects only), with a reply that says what
      happened to each. Checks that a file-system failure after the apply leaves a reported, recoverable state.
- [ ] **0.9** Undo and atomicity check: record how many undo steps one `TryApplyChanges` over several files makes in Visual Studio
      and what a closed file does; adjust section 2's "one undo" wording to what is true.
- [ ] **0.10** `CallerPropagator`: the walk that follows callers up `depth` levels, shared by 2.5 and 7.5.

### Phase 1: delete

- [ ] **1.1** `ide_delete` with `target: member`: a method, property, field, event, constructor, operator, indexer, enum member or
      interface member. Refused while any reference remains (its own body excluded), while it overrides or implements something,
      or while an interface member has implementers. `callerEdits` may remove the references. Tests that call it are listed;
      deleting a tested member is refused unless `deleteTests` names them. Removes its doc comment and the blank line it leaves.
- [ ] **1.2** `ide_delete` with `target: type`: top-level or nested. Refused while referenced from outside itself. `deleteFile`
      true deletes a file left with only usings (0.8).
- [ ] **1.3** `ide_list_unused`: read only. Private, internal and (when asked) public members and types with no references, from
      reference search plus IDE0051 and IDE0052. The search is scoped by accessibility (a private member searches only its type,
      an internal one its project and friends) so it stays fast. Notes reflection, serialization and DI as low confidence.

### Phase 2: change signature

- [ ] **2.1** `CallSiteRewriter`, part one: invocations, `new T(...)`, `base(...)` and `this(...)` initializers and member
      initializers, including calls inside the edited method itself. Extends `UseNode` beyond invocations.
- [ ] **2.2** `CallSiteRewriter`, part two: attributes, named and optional arguments, `params` expansion and extension-method
      calls. Method groups and delegate conversions are reported, not rewritten.
- [ ] **2.3** `ide_change_signature`: add, remove, reorder and rename parameters, with every call site rewritten. A new required
      parameter needs `valueForCallers` (an expression or `default`). A removed parameter's argument is dropped only when it has no
      side effects. Doc `param` tags follow.
- [ ] **2.4** `ide_change_signature`, part two: change a parameter's type, the return type, accessibility and modifiers, and
      change overrides and interface implementations in the same plan (replacing the related-member refusal).
- [ ] **2.5** `ide_thread_parameter`: add a parameter (for example `CancellationToken ct`) to a method and its callers up to `depth`
      (0.10), passing the caller's own value when it has one and `valueAtTop` at the top. Lists every method it changed.
- [ ] **2.6** `ide_edit_method` gains `rewriteCallers: true` when the signature change is a clean add, remove or reorder that it
      can map to a parameter list (the replacement text is parsed against the old one); anything else keeps `callerEdits`. A
      rename or retype is not guessed: it says to use `ide_change_signature`.

### Phase 3: more members

- [ ] **3.1** Constructors, destructors, operators and conversions: `ide_edit_method` and `ide_add_method` accept any
      `BaseMethodDeclarationSyntax`; the signature key and `EditTarget` cover them (`new T(...)`, operator uses).
- [ ] **3.2** Indexers and events through `ide_add_member` and `ide_edit_member`; delegates as type members.
- [ ] **3.3** Local functions: edit and add inside a method body; the enclosing method's references are unaffected.
- [ ] **3.4** Enum members: add and edit a value. Changing a value is refused when the enum is `[Flags]` or looks persisted unless
      `allowValueChange`. (Delete is 1.1.)
- [ ] **3.5** Interface members: add, with the list of every implementer. Stubs for implementers come from 4.3 (`implementers:
      "stub"` is enabled by that unit). (Delete is 1.1.)

### Phase 4: types

- [ ] **4.1** `ide_add_type`: class, struct, record, interface, enum or delegate in a new file (0.8), in the namespace and folder
      the project's convention implies or `namespace` and `folder`, else beside the anchor file with a warning. Modifiers, base
      list, attributes, generics, summary, and the usings it needs.
- [ ] **4.2** `ide_add_type` `withTests: true`: also adds a test class beside it in the test project, with the detected framework
      (xUnit, NUnit or MSTest).
- [ ] **4.3** `ide_implement_members`: stubs for the members an interface still needs, implicit or explicit, and overrides of chosen
      virtual members, with a `throw new NotImplementedException()` body in the type's layout. Enables `implementers: "stub"` in 3.5.
- [ ] **4.4** `ide_edit_type`: modifiers (`static`, `sealed`, `abstract`, `partial`, accessibility), base type and interface list,
      attributes, generic parameters and constraints. Reference rules apply (narrowing accessibility or adding `abstract` or
      `static` is checked against every use). All parts of a partial type are planned together. Kind change is 7.4, not here.
- [ ] **4.5** `ide_generate_members` `kind: constructor`: a constructor from chosen fields and properties, with assignment style.
      Null guards use 7.6's rewriter.
- [ ] **4.6** `ide_generate_members` `kind: equals`, `hashcode`, `tostring`, in the style the file already uses.

### Phase 5: moving things

- [ ] **5.1** `ide_move_member`, to another type: with references redirected.
- [ ] **5.2** `ide_move_member`, instance to static: refused when the body uses instance state unless `parameterize` passes it as a
      parameter; call sites are rewritten (2.1, 2.2).
- [ ] **5.3** `ide_move_member`, pull up to a base type and push down to a derived type.
- [ ] **5.4** `ide_organize_file`: split a type that shares a file into its own file, and rename a file to match its type (0.8).
- [ ] **5.5** `ide_partial`: merge the parts of a partial type into one file, or move chosen members into a new part.

### Phase 6: extract and inline

Which of these are bespoke depends on 0.0. Units for refactorings the spike shows Visual Studio already offers are replaced by
6.1 and 6.2.

- [x] **6.1** Dropped by the spike (0.0). `ide_list_code_actions` would need internal Visual Studio services.
- [ ] **6.1-old** `ide_list_code_actions`: read only. For a file and span (or a diagnostic id), the fixes and refactorings offered, each
      with an id and title (ids re-resolved at apply, as 0.0 finds).
- [ ] **6.2** `ide_apply_code_action`: apply one with the standard plan, checks and diff. Scope: document, project or solution for
      fix-all. Refuses actions whose operations are not plain text changes.
- [ ] **6.3** Tests on a scratch solution with known fixes (unused using, missing using, add braces).
- [ ] **6.4** `ide_extract` `kind: method`, analysis: a statement range, its parameters and return value from `AnalyzeDataFlow`;
      refuses more than one live-out, `yield`, and jumps out of the range.
- [ ] **6.5** `ide_extract` `kind: method`, rewrite: the new method, its doc comment shell (text from the caller) and the call that
      replaces the range; `break` and `return` handled where they can be.
- [ ] **6.6** `ide_inline_method`: replace a method's calls with its body and delete it; refused for recursion, overrides,
      interface members, and a body with several returns it cannot express as an expression.
- [ ] **6.7** `ide_extract` `kind: variable` and `kind: constant`.
- [ ] **6.8** `ide_extract` `kind: interface`: members of a type into a new interface that the type then implements.

### Phase 7: conversions

`ide_convert` with a `kind`; one unit per kind, one rewriter per kind, each with the reference check. Kinds that are code actions
per 0.0 are replaced by 6.2.

- [ ] **7.1** `field-to-property` (encapsulate field): the field becomes private, the property takes its name, references are
      redirected.
- [ ] **7.2** `property-to-method` and `method-to-property`, with call sites and `nameof` uses rewritten.
- [ ] **7.3** `auto-to-expression` and `expression-to-block` for members and local functions.
- [ ] **7.4** `class-to-record` and `class-to-struct` where lossless. This is the only kind-change tool.
- [ ] **7.5** `make-async`: adds `async`, the `Async` suffix on request, and awaits callers up to `depth` (0.10).
- [ ] **7.6** `add-null-guards`: argument checks at the top of chosen methods, in the style the file already uses.
- [ ] **7.7** `var-to-explicit`, `explicit-to-var` and `string-to-interpolation` over a member, a type or a file.

### Phase 8: cleanup and docs

Unused usings are handled by the built `ide_code_cleanup` and by 6.2 (IDE0005); there is no separate unit.

- [ ] **8.1** `ide_remove_unused`: takes the output of 1.3 and deletes the confirmed members and types through 1.1 and 1.2 in one
      change.
- [ ] **8.2** `ide_check_docs`: read only. XML doc comments that disagree with their member: missing, extra or misnamed `param`,
      `typeparam` and `returns` tags and `cref`s that do not resolve. Reuses the compiler's CS1572, CS1573 and CS1574 first and
      adds what they miss.
- [ ] **8.3** `ide_update_docs`: edit the doc comment of any member or type with no code change, in the layout it already has;
      adds one in the neighbours' style when asked and given text. Shares `MethodDocUpdater`.

### Phase 9: finish

- [ ] **9.1** Full gate; fix what it finds. Update `docs/dogfood.md` with the live-check results and gaps. Archive this plan.

## 5. Order and dependencies

0.0 first, and 0.1 to 0.5 before anything else. 0.2 is needed by 1.1 (it must locate every member kind). 1 and 2.1 to 2.2 next:
2.3 to 2.6 and 5.2 need the rewriter, and 8.1 needs 1. 4.3 comes before the stub option of 3.5. 3 and 4 are otherwise independent.
7 needs 2.1 and 2.2. The spike result may remove units from 6, 7 and 8. 9 last.

## 6. Risks to watch

- Roslyn from the VS install (net48): some APIs the plan names may be internal. 0.0 and the first unit of each phase prove them.
- `SymbolFinder` over a large solution: scoped searches (1.3), a timeout and `incomplete` (0.4).
- Refactoring tools that look safe but are not: each refuses the cases it cannot prove (jumps out of an extracted range, side
  effects in a removed argument, instance state in a static move).
- Closed or unsaved files, linked files and multi-targeting: section 3 and 0.6.
- A file-system step after the apply (move, create, delete) can fail: 0.8 and 0.9.

## 7. Sonnet review of revision 2, and what changed

- 0.1 and 0.3 were narrowed to what really repeats (0.2, 0.3); namespace and type moves do not use the coverage helper.
- 2.4 as written could not map a rename or a reorder from replacement text; it is now 2.6 with a stated limit.
- Added: planning off the UI thread (0.4), plan hash (0.5), multi-target, linked, generated and read-only files (0.6), file create
  and delete (0.8), the undo check (0.9), a shared caller propagator (0.10).
- Units split: 2.1, 2.2, 4.1, 4.4 to 4.6, 5.1 to 5.3, 6.4 to 6.8 and 7.x.
- Tests moved into each unit; the end-of-phase test units are gone. The live check is per phase.
- Order fixed: spike first, test helper first, 0.2 before 1.1, 4.3 before the stub option of 3.5.
- Overlaps removed: class-to-record only in 7.4, null guards only in 7.6, unused usings left to `ide_code_cleanup`, deletes only in
  1.1, one tool per `kind` family (about 20 tools).
- Not verified by the reviewer, and so open for the spike and first units: Roslyn and VS versions, whether `TryApplyChanges` leaves
  closed files unsaved, and the behaviour of `MethodTestRunner` after deletes.
