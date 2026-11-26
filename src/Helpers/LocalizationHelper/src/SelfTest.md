# LocalizationHelper self test

Tools: `StringScan`, `ResxCheck`, `LocalizationSyncCheck`, `LocalizationSync`, `LocalizationExtract`, `LocalizationCultureCheck`, `LocalizationPseudo`, `LocalizationTranslate`.

## StringScan and ResxCheck

Fixture: a scratch folder with `A.cs` holding `var a = "Could not read the file.";` and `Resources/Strings.resx` holding two entries named `One` that are both unused in code.

- `StringScan` (path = fixture folder): PASS when `ok` is true and `findings` has one entry for `A.cs` line 1 with the literal text.
- `StringScan` (path = a folder that does not exist): PASS when `ok` is false and `error` names the folder.
- `ResxCheck` (root = fixture folder): PASS when `ok` is true and `findings` includes a `duplicate` for `One`.
- `ResxCheck` (root = a folder that does not exist): PASS when `ok` is false and `error` names the folder.


## Culture sync

Fixture: a scratch folder with `P/Resources/Strings.resx` holding keys `A` ("One") and `B` ("Two"), and `P/Resources/Strings.de.resx` holding `A` ("One") and an extra key `C`.

- `LocalizationSyncCheck` (root = fixture folder): PASS when `findings` has `missing` for `B`, `extra` for `C` and `untranslated` for `A`, all with culture `de`.
- `LocalizationSync` (root = fixture folder, no confirm): PASS when the reply has `plan` true, a 32-character `token` and one `add-stub` change for `B`, and `Strings.de.resx` is unchanged.
- `LocalizationSync` (confirm = that token): PASS when `applied` is true and `Strings.de.resx` now holds `B` with the neutral text and the comment `needs-translation`.
- `LocalizationSync` (same token again): PASS when `ok` is false.
- `LocalizationSyncCheck` (root = fixture folder, after the confirm): PASS when `missing` no longer lists `B`.

## Extract

Fixture: a scratch folder with `P/Strings.cs` (`namespace P;` and an empty internal static class `Strings`), `P/Resources/Strings.resx` with no entries, and `P/A.cs` in `namespace P;` holding `const string C = "Keep this text.";` and `string M() => "Could not read it.";`.

- `LocalizationExtract` (path = fixture folder, no confirm): PASS when `plan` is true, `items` has one entry with `replacement` `Strings.Get("CouldNotReadIt")`, `skipped` has one entry with reason `const-context`, and no file changed.
- `LocalizationExtract` (confirm = that token): PASS when `A.cs` now holds the `Strings.Get` call and `Strings.resx` holds `CouldNotReadIt`.
- `StringScan` (path = the `P` folder): PASS when only the `const` literal remains.


## Culture check

Fixture: a scratch folder with `C.cs` holding `class C { string M(int i) => i.ToString(); }` and `D.cs` holding `class D { string M(int i) => i.ToString(); } // culture-ok`.

- `LocalizationCultureCheck` (path = fixture folder): PASS when `ok` is true and `findings` has one entry for `C.cs` line 1 with kind `culture-call` and call `i.ToString()`, and none for `D.cs`.
- `LocalizationCultureCheck` (path = a folder that does not exist): PASS when `ok` is false and `error` names the folder.


## Pseudo

Fixture: a scratch folder with `P/Resources/Strings.resx` holding `A` ("Hello {0}") and `B` ("Goodbye").

- `LocalizationPseudo` (root = fixture folder, resx = `P/Resources/Strings.resx`, no confirm): PASS when `plan` is true, `count` is 2, `changes` keeps `{0}` in the pseudo text for `A`, and `Strings.qps-ploc.resx` does not exist.
- `LocalizationPseudo` (same arguments plus confirm = the token): PASS when `applied` is true and `Strings.qps-ploc.resx` holds two entries with the comment `pseudo-localized`.
- `LocalizationSyncCheck` (root = fixture folder): PASS when `findings` is empty (the pseudo culture is skipped).
- Delete `Strings.qps-ploc.resx` afterwards; it is generated and not committed.


## Translate

Fixture: a scratch folder with `P/Resources/Strings.resx` holding `A` ("Hello {0}") and `P/Resources/Strings.de.resx` holding `A` ("Hello {0}") with the comment `needs-translation`.

- `LocalizationTranslate` (root = fixture folder, culture = `de`, no confirm): PASS when `plan` is true, `items` lists `A`, `batches` is 1, `sampling_available` says whether this client supports sampling, and `Strings.de.resx` is unchanged.
- `LocalizationTranslate` (same arguments plus confirm = the token): PASS when the client supports sampling and `applied` is true with `A` written, comment `machine-translated`, and `{0}` kept. SKIPPED with the reason when the client has no sampling: PASS then means `ok` is false with an error naming sampling and the file unchanged.

Record each as PASS, FAIL or SKIPPED with the exact arguments and reply.
