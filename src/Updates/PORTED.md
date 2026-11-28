# Ported from VideoForensics

Source: `F:\videoforensics`, commit `7e47bd9b`, folder `src/client/host/VideoForensics.Hosting/src/Updates/`.

| Here | Source | Changes |
|---|---|---|
| `src/UpdateManifest.cs` | `UpdateManifest.cs` | Namespace `AiDevHelpers.Updates`. Unchanged otherwise. |
| `src/UpdatePathGuard.cs` | `UpdatePathGuard.cs` | Namespace only. |
| `src/UpdatePackageVerifier.cs` | `UpdatePackageVerifier.cs` | Namespace only. |
| `src/Ed25519PackageSignatureVerifier.cs` | `Ed25519PackageSignatureVerifier.cs` | The VideoForensics release public key is not carried over. The key is passed in (dev key from machine-only config; unit 10b key for release). |

| `src/UpdateApplier.cs` | `UpdateApplier.cs` | Namespace only. Dropped unused `destination` values and the `_ = destination` no-ops; the `IsKept` comment now names `Data`/`DataTools`. |
| `src/UpdateStager.cs` | `UpdateStager.cs` | Namespace only. Version folder name must match `^[0-9A-Za-z][0-9A-Za-z.+-]*$`. |

Unit 10b (source as above, pinned to `7e47bd9b`):

| Here | Source | Changes |
|---|---|---|
| `src/UpdateDrain.cs` (`McpUpdateDrain`, `NoOpUpdateDrain`) | `MaintenanceUpdateDrain.cs` | Drains MCP.Http through `IMcpProcessControl` (the Host's hold gate). Timeout lives in the control. |
| `src/StagedUpdateInstaller.cs` | `StagedUpdateInstaller.cs`, `StartupUpdateApplier.cs` | Startup path folded into `InstallNewestAsync`. Empty keep list (install folder holds program files only). Rejected folders end in `.rejected`; dot folders are ignored. Status in `status.json`. |
| `src/GitHubReleaseClient.cs` | `UpdateCheckService.cs` (asset lookup) | Package zip for the tag's version only; drafts and prereleases skipped; `User-Agent` sent. |
| `src/UpdateDownloader.cs` | `StagedUpdateInstaller.cs` (download) | Plain `HttpClient`. https and allowed hosts, also after redirects; 1 GB cap; size check; version must match the tag. |
| `src/UpdateSources.cs` | (new) | Interfaces so the Host watcher tests use fakes. |
| `src/UpdateManifestWriter.cs`, `tools/AiDevHelpers.Updates.ManifestTool` | (new) | Release-time manifest and signature check. |
| `src/ReleaseSigningKeys.cs` | (new) | Release public key. Private half is the `RELEASE_SIGNING_KEY` secret. |
| `src/Tray/Host/src/StagedUpdateWatcher.cs` | `StagedUpdateWatcher.cs` | Exits non-zero after an applied install (service recovery restarts it). |
| `src/Tray/Host/src/StartupUpdateService.cs` | (new) | Locks the updates folder and installs before the supervisor starts. |
| `src/Tray/Host/src/McpHold.cs`, `McpProcessControl.cs` | (new) | Supervisor hold gate; the adapter behind `IMcpProcessControl`. |

Not ported: the VideoForensics service and UI wiring.

Pinned by tests: `src/Updates/tests/UpdatePackageVerifierTests.cs`, `Ed25519PackageSignatureVerifierTests.cs`, `UpdateManifestTests.cs`, `UpdatePathGuardTests.cs`.

