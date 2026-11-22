# LintHelper self test

Tools: `LintCheck`.

## Full self test

Fixture: a scratch folder with a `.editorconfig` that lacks `root = true`, a `tsconfig.json` without `"strict": true`, and a `package.json` that lists `typescript` with no ESLint config.

- `LintCheck` (root = fixture folder, runDotnetFormat = false): PASS when `ok` is true, and `findings` names the missing `root = true`, the missing strict mode, and the missing ESLint config.
- `LintCheck` (root = a folder that does not exist): PASS when `ok` is false and `error` names the folder.

Record each as PASS or FAIL with the exact arguments and reply.
