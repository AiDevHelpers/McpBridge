# Installer, then the update path for testing

Branch from `wip`; push to `wip` at the end of each unit (pull `wip` first; no PR). Decided 2026-11-27:
- Installer: Inno Setup 6 (`ISCC.exe` at `C:\Program Files (x86)\Inno Setup 6\`).
- Update signing: a local development key for debug-build packages only. Release signing waits for the user's key (unit 10b).
- Scope: installer first, then the update path, so the installer can be updated in place for testing.

## Units

### I1. Package build (`scripts/build-package.ps1`)
- Publishes `AiDevHelpers.Host`, `AiDevHelpers.MCP.Http` and `AiDevHelpers.Tray` (Release) into `artifacts/package/`.
- Writes `artifacts/package/version.txt` with the `-Version` parameter.
- Done: the folder holds the three exes and their dependencies, and `version.txt`. Checked by running the script and listing the folder.

### I2. Installer (`installer/AiDevHelpers.iss`, `scripts/build-installer.ps1`)
- Installs the package to `%ProgramFiles%\AiDevHelpers` and runs the pinned `scripts/service-install.ps1 -SourceFolder` (the service runs as LocalSystem, auto start).
- The tray starts at logon: HKCU Run value `AiDevHelpers`, pointing at the tray exe.
- Uninstall runs the pinned `service-uninstall.ps1` and removes the Run value.
- Done: silent install and uninstall work on this machine; the service and the Run value are checked before and after.

### U1. Update path (next round)
- A package manifest (file hashes, version) signed with the development key; the service verifies it and applies from a SYSTEM-owned copy. Design from `docs/plans/2026-11-27-tray-consolidated.md` (units 10a and 10b).
- Done: an updated package installs over the installed one, with a rejected run for a tampered file.

## Open
- Release signing key (unit 10b): the user provides it when ready.
