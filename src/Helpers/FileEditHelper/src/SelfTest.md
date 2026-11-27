# FileEditHelper self test

Tools: `FileEdit`.

## Full self test

Fixture: a scratch folder with a text file `sample.txt` containing `alpha beta alpha`.

- `FileEdit` (action = `apply`, filePath = `sample.txt`, oldText = `beta`, newText = `gamma`, dryRun = true, root = fixture): PASS when `ok` is true, `dryRun` is true, and the file is unchanged.
- `FileEdit` (action = `apply`, same arguments, dryRun = false): PASS when `ok` is true, the file reads `alpha gamma alpha`, and `backupPath` exists.
- `FileEdit` (action = `rollback`, backupPath from the previous reply, targetFilePath = `sample.txt`): PASS when `ok` is true and the file reads `alpha beta alpha`.
- `FileEdit` (action = `apply`, filePath = `../outside.txt`): PASS when `ok` is false and `error` says the path is outside the workspace root.

Record each as PASS, FAIL or NOT RUN with the exact arguments and reply.
