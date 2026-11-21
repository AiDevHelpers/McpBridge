# BookmarkHelper self test

Tools: `BookmarkList`, `BookmarkGet`, `BookmarkSet`, `BookmarkRemove`.

Visual Studio only. VS Code returns not-supported, which is expected. See ide-bridge.md Limits.

## Full self test

Fixture: a Visual Studio instance with a scratch `.cs` file open.

- `BookmarkSet` (file = fixture, line = 1, name = `self test`): PASS when `ok` is true and the reply has an `id`.
- `BookmarkList`: PASS when `ok` is true and `items` includes the bookmark just set.
- `BookmarkGet` (id from BookmarkSet): PASS when `ok` is true and the file and line match. `name` is null, because VS does not store names.
- `BookmarkRemove` (id from BookmarkSet): PASS when `ok` is true.
- `BookmarkGet` (id from BookmarkSet, after removal): PASS when `ok` is false and `error` says it was not found.

Record each as PASS, FAIL or NOT RUN with the exact arguments and reply. Record NOT RUN when no Visual Studio instance is open.
