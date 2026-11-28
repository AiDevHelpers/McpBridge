# Ported from VideoForensics

Source: `F:\videoforensics`, commit `7e47bd9b`, folder `src/client/host/VideoForensics.Hosting/src/Updates/`.

| Here | Source | Changes |
|---|---|---|
| `src/UpdateManifest.cs` | `UpdateManifest.cs` | Namespace `AiDevHelpers.Updates`. Unchanged otherwise. |
| `src/UpdatePathGuard.cs` | `UpdatePathGuard.cs` | Namespace only. |
| `src/UpdatePackageVerifier.cs` | `UpdatePackageVerifier.cs` | Namespace only. |
| `src/Ed25519PackageSignatureVerifier.cs` | `Ed25519PackageSignatureVerifier.cs` | The VideoForensics release public key is not carried over. The key is passed in (dev key from machine-only config; unit 10b key for release). |

| `src/UpdateApplier.cs` | `UpdateApplier.cs` | Namespace only. Dropped unused `destination` values and the `_ = destination` no-ops; the `IsKept` comment now names `Data`/`DataTools`. |

Not yet ported: `UpdateStager.cs`, `StagedUpdateWatcher.cs`, `StartupUpdateApplier.cs`, `StagedUpdateInstaller.cs`, `MaintenanceUpdateDrain.cs`.

Pinned by tests: `src/Updates/tests/UpdatePackageVerifierTests.cs`, `Ed25519PackageSignatureVerifierTests.cs`, `UpdateManifestTests.cs`, `UpdatePathGuardTests.cs`.

