# LocalizationHelper self test

Tools: `StringScan`, `ResxCheck`, `LocalizationSyncCheck`, `LocalizationSync`, `LocalizationExtract`, `LocalizationCultureCheck`.

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

Record each as PASS or FAIL with the exact arguments and reply.
