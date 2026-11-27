# file_edit contract

Behavior ported from TokenSaver `PatchEngine` (`TokenSaver.CodeHelper/Patching/PatchEngine.cs`) and `CodeHelperTools.EditFile`.
Differences by design: results are JSON (`{ok, error, ...}`), no re-indexing, backup folder `.aidevhelpers/backups`.

## Tool
`file_edit(action, filePath?, root?, dryRun?, description?, ...)`. `action` is trimmed and case-insensitive. Unknown action:
`ok=false`, error lists `apply, insert, delete, rename, rollback`.

| action | parameters (required first) | behavior |
|---|---|---|
| apply | filePath, oldText, newText, occurrence=0 | Replace `oldText` with `newText`. 0 replaces all; N replaces the Nth match (ordinal, case-sensitive). Empty `oldText`: "OldText must not be empty." Missing match: "Anchor not found in <file>: '<first 60 chars>…'". Nth missing: "Occurrence N of anchor not found in <file>." |
| insert | filePath, anchor, content, position=after | Insert `content` before or after the first `anchor`. Empty anchor: "Anchor must not be empty." Anchor missing: error, unless `RequireAnchorMatch` is off, then append at end. |
| delete | filePath, pattern, isRegex=false | Remove literal text or regex matches. Regex uses `Multiline`; bad regex: "Invalid regex: <msg>". Literal not found: "Pattern not found in <file>: '…'". |
| rename | filePath, oldName, newName, wholeWordOnly=true, replaceInComments=false | Replace identifier. Whole word uses `\b…\b`. Without `replaceInComments`, lines whose first non-blank text starts with `//`, `/*` or `*` are skipped. No matches: "'<old>' not found in <file>." |
| rollback | backupPath, targetFilePath | Copy a backup over the target. Source must be inside the backup root; target inside the workspace root. Returns `ok=false` otherwise. |

Missing parameter text: "<name> is required for action '<action>'." Applies to filePath, oldText, newText, anchor, content, pattern, oldName, newName, backupPath, targetFilePath.

## Write path (all mutating actions)
1. Resolve the path with the workspace guard (see below).
2. File must exist: "File not found: <path>".
3. Read bytes; detect encoding: UTF-8 BOM, UTF-16 LE BOM, UTF-16 BE BOM, else UTF-8 without BOM. Encoding is kept on write.
4. Apply the operation in memory. Error: no write.
5. `dryRun=true`: return `ok=true`, `dryRun=true`, `linesChanged`; write nothing and create no backup.
6. Backup: byte-exact copy to `<root>/.aidevhelpers/backups/<yyyyMMdd_HHmmss_fff>_<6 hex>_<path with / \ : replaced by _>`. Created with `CreateNew`. Failure: "Backup failed: <msg>", file untouched.
7. Atomic write: temp file `<file>.<guid>.tmp` in the same folder, then `File.Replace`, retried up to 5 times with 25 ms steps on `IOException`. Temp file is always removed.
8. Result: `ok`, `filePath`, `linesChanged`, `backupPath`, `description`.

`linesChanged`: apply = lines in `oldText`; insert = lines in `content`; literal delete = lines in `pattern`; regex delete = line-count difference; rename = match count. Lines = count of `\n` + 1.

## Workspace guard (port of `WorkspacePathGuard`)
- Path is absolute, or relative to the root. Empty: "A path is required."
- No root: "No workspace root is known for this session. Pass 'root'." No cwd fallback. Root comes from `root`, else the repo root found from the current folder.
- Symlinks and junctions are followed; the real location must be inside the real root. Otherwise: "Path '<p>' is outside the workspace root '<root>'."
- Rollback source must resolve under the backup root; the target under the workspace root.

## Batch
The engine supports a batch that stops at the first failure. The tool exposes single operations only.

## Config
`RequireAnchorMatch` (default true). Backup folder name fixed at `.aidevhelpers/backups` in v1.
