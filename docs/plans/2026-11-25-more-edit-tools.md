# More C# edit tools: implementation plan, 2026-11-25

Status: revision 1. Not reviewed, not started. Each unit is one commit with a checklist.

## 1. Scope

The C# edit tools that the Visual Studio bridge does not have yet. Already built (see `docs/ide-bridge.md`): `ide_edit_method`,
`ide_add_method`, `ide_add_overload`, `ide_add_member`, `ide_edit_member`, `ide_tdd_add_test`, `ide_tdd_update_test`,
`ide_rename_namespace`, `ide_move_type`, `ide_rename_symbol`, `ide_replace_in_files`.

Adds: safe delete, change signature with call-site rewrite, the non-method members (constructors, operators, indexers, local
functions, finalizers, events, delegates, enum and interface members), type creation and type-level edits, interface and override
generation, moving members and splitting files, extract and inline refactorings, conversions, code fixes and cleanup, and docs-only
edits.

Out of scope: anything outside C# code (project files, NuGet, XAML, resx, JSON, VB, F#) and VS Code (TypeScript and JavaScript)
equivalents. Every tool here is Visual Studio only and says so in its description.

## 2. Decisions

- Every tool is plan, then confirm, as the built ones are: no `confirm` returns the checks and changes nothing.
- **Reference check on every member, any accessibility.** A change other code can feel is refused unless every reference is
  covered, either by a `callerEdits` entry or, from phase 2 on, by the call-site rewriter. A delete is refused while any reference
  remains. Private members are checked too.
- Never invent text: doc comments, names and default values come from the caller. A missing one is a warning or a refusal, as it
  is in the built tools.
- Engines are pure functions of a Roslyn `Solution`, tested on an `AdhocWorkspace`; the bridge applies the result in one
  `TryApplyChanges`, so one undo covers a change.
- After an applied change the MCP server runs the tests that call the thing, with coverage (`MethodTestRunner`).
- Roslyn code actions are reached by a spike (6.0) before any bespoke engine is built for a refactoring Visual Studio already has.
  If the spike works, phases 6 to 8 mostly become thin tools over code actions; if not, the bespoke engines are the plan.

## 3. Cross-cutting contracts

- **Reply shape** is the built one: `{planned, applied, check:{refused, reasons, files, warnings, new_errors, new_warnings,
  new_notices, tests, method, ...}}`. New fields are added, none renamed.
- **Format and diagnostics:** the file the change is anchored in is formatted with the file's own line ending; diagnostics new
  since the change are reported, errors refuse it. Tools that can touch many files do not format (as the namespace tools do).
- **Preview:** from unit 0.2 every plan carries `diff`, a unified diff per changed file (capped), so the caller can show the user
  the change without applying it.
- **Docs:** a doc comment follows its member (`MethodDocUpdater`). Text comes from the caller.
- **Strings** go in `Resources/Strings.resx`; literals that are not user text go in the hard-coded-strings allowlist with a reason.
- **Each unit** ends with: engine, bridge action, MCP tool, rows in `docs/ide-bridge.md` and `mcp-tools.md`, a self-test line, a
  conformance fixture, tests, lite gate, commit, push.
- **Limits:** a reference search or a rewrite over a large solution honours a timeout and says so (0.4).

## 4. Phases

### Phase 0: shared parts

- [ ] **0.1** `EditPipeline`: one place for the steps the engines repeat (apply changes, format, new diagnostics, test targets,
      method location, docs). Existing engines move onto it with no behaviour change; their tests stay green.
- [ ] **0.2** `diff` in every plan reply (unified, per file, capped at 40 files and 400 lines each, with a truncation note).
- [ ] **0.3** `CoverageOfReferences`: the "every reference covered by a caller edit or a rewrite" rule as one helper, used by method,
      member, namespace and type engines. Covers overrides and interface implementations as related members.
- [ ] **0.4** Time and size guard: `SymbolFinder` calls take a cancellation token tied to a timeout (default 60 s, `timeoutSeconds`
      argument); the reply says `incomplete: true` and refuses when a search did not finish.
- [ ] **0.5** `TestSolutions`: one test helper for multi-file, multi-project `AdhocWorkspace` solutions with framework stand-ins
      (replaces the copies in the test files).

### Phase 1: delete

- [ ] **1.1** `ide_delete_member`: delete a method, property, field, event, constructor, operator or indexer. Refused while any
      reference remains (own body excluded), while it overrides or implements something, or is an interface member with
      implementers. `callerEdits` may delete or rewrite the references. Tests that call it are listed; deleting a tested method is
      refused unless `deleteTests` names them. Removes its doc comment and the blank line it leaves.
- [ ] **1.2** `ide_delete_type`: delete a top-level or nested type. Refused while referenced outside itself. With `deleteFile`
      true, a file left with nothing but usings is deleted on disk (SDK-style projects only, as `FileMover`).
- [ ] **1.3** `ide_list_unused`: read only. Private and internal members and types with no references, from reference search plus
      the IDE0051 and IDE0052 diagnostics, with a confidence note for reflection, serialization and DI. Feeds 1.1 and 1.2.
- [ ] **1.4** Tests: delete with and without references, partial type, interface member, test method, enum member, file deletion.

### Phase 2: change signature

- [ ] **2.1** `CallSiteRewriter`: rewrites invocations, object creations, `base` and `this` initializers, attributes, named
      arguments and `params` expansion for a parameter list change (add, remove, reorder, rename, retype with a conversion
      expression). Method groups and delegate conversions are reported, not rewritten.
- [ ] **2.2** `ide_change_signature`: add, remove, reorder, rename or retype parameters, change the return type, and change
      accessibility and modifiers, with every call site rewritten. A new required parameter needs `valueForCallers` (an
      expression, or `default`); a removed parameter's argument is dropped only when it has no side effects, else refused. Doc
      `param` tags follow. Overrides and interface implementations are changed in the same plan.
- [ ] **2.3** `ide_thread_parameter`: add a parameter (for example `CancellationToken ct`) to a method and to its callers up to
      `depth` levels, passing the caller's own value when it has one and `valueAtTop` at the top. Refuses on lambdas that cannot
      take it, and lists every method it changed.
- [ ] **2.4** `ide_edit_method` stops refusing a signature change that this unit can rewrite: it offers `rewriteCallers: true`
      and keeps `callerEdits` for the cases the rewriter reports.
- [ ] **2.5** Tests: each parameter operation, named and optional arguments, `params`, extension methods, constructors, overrides,
      interface members, generic methods, method groups reported.

### Phase 3: more members

- [ ] **3.1** Constructors, destructors, operators and conversions: `ide_edit_method` and `ide_add_method` accept any
      `BaseMethodDeclarationSyntax`; the signature key and the reference rules cover them (`new T(...)`, operator uses).
- [ ] **3.2** Indexers, events and delegates through `ide_add_member` and `ide_edit_member` (events add and edit; delegates as type
      members).
- [ ] **3.3** Local functions: edit and add inside a method body, with the enclosing method's references unaffected.
- [ ] **3.4** Enum members: add, edit value, delete. Changing a value is refused when the enum is serialized (`[Flags]` and
      persisted values are warned about) unless `allowValueChange`.
- [ ] **3.5** Interface members: add (lists every implementer; `implementers: "stub"` adds stubs through 4.3), edit, delete.
- [ ] **3.6** Tests for each.

### Phase 4: types

- [ ] **4.1** `ide_add_type`: class, struct, record, interface, enum or delegate in a new file, in the folder and namespace the
      project's convention implies (or `namespace` and `folder`), with modifiers, base list, attributes, generics, doc summary and
      the usings it needs. `withTests: true` also adds a test class beside it in the test project with the detected framework.
- [ ] **4.2** `ide_edit_type`: modifiers (`static`, `sealed`, `abstract`, `partial`, accessibility), base type and interface list,
      attributes, generic parameters and constraints, and kind change (class to record, class to struct) where it is lossless.
      Reference rules apply: narrowing accessibility or adding `abstract` or `static` is checked against every use.
- [ ] **4.3** `ide_implement_interface`: generate stubs for the members an interface still needs, implicit or explicit, with a
      `throw new NotImplementedException()` body, in the type's layout; `ide_override_members` does the same for chosen virtual
      members.
- [ ] **4.4** `ide_add_constructor`: from chosen fields and properties, with optional null guards and assignment style; also
      `ide_generate_members` for `Equals`, `GetHashCode` and `ToString`.
- [ ] **4.5** Tests for each, including the test-class scaffold on xUnit, NUnit and MSTest.

### Phase 5: moving things

- [ ] **5.1** `ide_move_member`: to another type, including making it static, pull up to a base type and push down to a
      derived type. Instance to static is refused when the body uses instance state, unless that state is passed as a parameter
      (`parameterize`), and call sites are rewritten for it (2.1).
- [ ] **5.2** `ide_split_type_to_file`: put a type that shares a file into its own file; `ide_rename_file_to_type`: rename a file to
      match its type. Both move files on disk for SDK-style projects only.
- [ ] **5.3** `ide_merge_partial` and `ide_split_partial`: fold the parts of a partial type into one file, or move chosen members
      into a new part.
- [ ] **5.4** Tests for each.

### Phase 6: extract and inline

- [ ] **6.0** Spike, one day: can the extension reach Roslyn's code fix and code refactoring services through MEF
      (`ICodeFixService`, `ICodeRefactoringService`) from the net48 Core project? Write down what compiles and what runs.
      If it does: units 6.1 to 6.3 become `ide_list_code_actions` and `ide_apply_code_action`, and 6.4 to 6.6 are built only for
      the refactorings the service lacks. If it does not: 6.4 to 6.6 are bespoke.
- [ ] **6.1** `ide_list_code_actions`: read only. For a file and span (or a diagnostic id), the fixes and refactorings Visual
      Studio offers, each with an id and title.
- [ ] **6.2** `ide_apply_code_action`: apply one by id, with the standard plan, checks and diff. Scope: document, project or
      solution for fix-all.
- [ ] **6.3** Tests on a scratch solution with known fixes (unused using, missing usings, add braces).
- [ ] **6.4** `ide_extract_method`: a statement range becomes a method; parameters and return value come from
      `AnalyzeDataFlow`; refuses when the range has more than one live-out, a `yield`, or jumps out of it (`break` and `return`
      are handled where they can be).
- [ ] **6.5** `ide_inline_method`: replace a method's calls with its body and delete it; refused for recursion, for overrides and
      interface members, and for a body with several returns it cannot express as an expression.
- [ ] **6.6** `ide_extract_variable`, `ide_extract_constant` and `ide_extract_interface` (members of a type into a new interface
      that the type then implements).
- [ ] **6.7** Tests for each.

### Phase 7: conversions

Each is `ide_convert` with a `kind`, one unit per kind, one rewriter per kind, each with the reference check.

- [ ] **7.1** `field-to-property` (encapsulate field): the field becomes private, the property takes its name and the references
      are redirected.
- [ ] **7.2** `property-to-method` and `method-to-property`, with call sites and `nameof` uses rewritten.
- [ ] **7.3** `auto-to-expression` and `expression-to-block` for members and local functions.
- [ ] **7.4** `class-to-record` and `make-async` (adds `async`, the `Async` suffix on request, and awaits the callers up to
      `depth`).
- [ ] **7.5** `var-to-explicit` and `explicit-to-var` over a member, a type or a file; `string-to-interpolation`.
- [ ] **7.6** `add-null-guards`: argument checks at the top of chosen methods, in the style the file already uses.
- [ ] **7.7** Tests for each.

### Phase 8: cleanup and docs

- [ ] **8.1** `ide_remove_unused_usings` and `ide_organize_usings`: file, project or solution scope; honours global usings.
- [ ] **8.2** `ide_remove_unused_members`: takes the output of 1.3, deletes the confirmed ones through 1.1 and 1.2 in one change.
- [ ] **8.3** `ide_check_docs`: read only. XML doc comments that disagree with their member: missing, extra or misnamed
      `param`, `typeparam` and `returns` tags, `cref`s that do not resolve.
- [ ] **8.4** `ide_update_docs`: edit a member's doc comment with no code change, in the layout it already has; adds a comment in
      the neighbours' style when asked and given text.
- [ ] **8.5** Tests for each.

### Phase 9: finish

- [ ] **9.1** Reinstall the VS extension (`scripts/update-vs-extension.ps1`, one installer run) and live-check every tool in a
      scratch solution with a real test project; record results and gaps in `docs/dogfood.md`.
- [ ] **9.2** Run the full gate and fix what it finds. Archive this plan.

## 5. Order and dependencies

0 first. 1 and 2.1 next, because 2.2 and 5.1 need the rewriter and 8.2 needs 1. 3 and 4 are independent of each other. 6.0 can run
any time and decides how much of 6 to 8 is bespoke work, so run it early. 7 needs 2.1. 9 last.

## 6. Open questions (need the user)

- **Q1** Delete: should `ide_delete_member` offer to delete the callers too (`deleteCallers`), or only refuse? Recommended: refuse
  and list; callers are removed through `callerEdits`.
- **Q2** Change signature: a new required parameter has no value for old callers. Recommended: refuse without `valueForCallers`.
- **Q3** Type creation: when the project's folders do not follow its namespaces, put the new file next to the anchor file?
  Recommended: yes, and warn.
- **Q4** Order: is phase 2 (change signature) before phase 1 (delete) wanted? Recommended order is as written.
- **Q5** If the 6.0 spike fails, build 6.4 to 6.6 bespoke, or drop them? Recommended: build extract method and inline method only.
