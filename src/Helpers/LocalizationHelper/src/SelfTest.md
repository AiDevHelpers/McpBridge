# LocalizationHelper self test

Tools: `StringScan`, `ResxCheck`.

## StringScan and ResxCheck

Fixture: a scratch folder with `A.cs` holding `var a = "Could not read the file.";` and `Resources/Strings.resx` holding two entries named `One` that are both unused in code.

- `StringScan` (path = fixture folder): PASS when `ok` is true and `findings` has one entry for `A.cs` line 1 with the literal text.
- `StringScan` (path = a folder that does not exist): PASS when `ok` is false and `error` names the folder.
- `ResxCheck` (root = fixture folder): PASS when `ok` is true and `findings` includes a `duplicate` for `One`.
- `ResxCheck` (root = a folder that does not exist): PASS when `ok` is false and `error` names the folder.

Record each as PASS or FAIL with the exact arguments and reply.
