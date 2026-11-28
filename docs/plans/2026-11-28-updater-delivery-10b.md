# Updater delivery (unit 10b)

Parent plan: `docs/plans/2026-11-27-tray-consolidated.md` (row 10b). Depends on unit 10a (`feat/tray-unit10a-updater-core`, PR #134). Branch 10b from that branch until #134 merges into `wip`, then rebase on `wip`.

## Decisions (answered 2026-11-28)

- The watcher, installer and startup applier run in the **Host service** (LocalSystem, always on). Drain gates on the MCP.Http process the Host supervises.
- A release publishes a **zip** on GitHub Releases: the package contents with no base folder (`manifest.json`, `manifest.json.sig` and the files at the zip root). The stager (10a) verifies it.
- Downloads use a **plain `HttpClient`** (no `IHttpClientFactory`, no new package).
- **Install folder (`%ProgramFiles%\AiDevHelpers`) holds program files only.** Config and data stay in `%ProgramData%\AiDevHelpers` (`mcp-http\settings.json`, `logs`, `updates`). So the applier's keep list is **empty**, and the applier never writes outside the install folder. Unit 5 tests this.
- The release public key is `ReleaseSigningKeys.Current()` (10a, `8a42ce9`). The private key is the `RELEASE_SIGNING_KEY` secret of the protected `release` environment (created and set 2026-11-28; reviewer: the repo owner).
- Dev packages stay debug-only (10a rule).
- An update restarts the Host: after a successful apply the Host exits non-zero, so the service recovery action restarts it on the new files.

## Review findings folded in (Sonnet review, 2026-11-28)

- **Drain needs a supervisor hold.** `McpSupervisor` restarts a killed MCP.Http in about 1 s and gives up after 8 fast exits. Add a hold gate (Unit 4a).
- **Drain is a kill, not a wait.** In-flight MCP requests are cut off. Say so in the release notes.
- **Host restart after apply.** The Host cannot replace its own running files, so it exits non-zero after apply (Unit 6 seam).
- **Reject marker.** A folder that fails verify or apply is renamed `*.rejected` and not retried. `FindNewestStaged` skips `.rejected`, `.download` and `.staging`.
- **Download caps.** `ResponseHeadersRead`; abort above 1 GB; reject a Content-Length above the cap; https only; allow-listed hosts (`github.com`, the GitHub release-asset host) checked after redirects; fixed local file name.
- **Tag spoofing.** Require the staged `Version` to equal the tag's version. Only non-draft, non-prerelease releases. Send a `User-Agent` and `Accept` header.
- **Expiry.** The writer sets `NotAfter`, so an old signed package cannot be replayed.
- **Signing.** `openssl pkeyutl -sign -rawin -inkey <pem> -in manifest.json -out manifest.json.sig`, check `$LASTEXITCODE`, check the output is 64 bytes, write bytes without a pipe. Verify the produced signature with `Ed25519PackageSignatureVerifier` and `ReleaseSigningKeys.Current()` as a workflow step.
- **Host wiring.** Run `UpdatesFolderAcl.Apply` before the first download. Add a ProjectReference to `AiDevHelpers.Updates` from the Host. Run `StartupUpdateApplier` before `McpSupervisor` starts (synchronously in `Main`). Update the Host header comment.
- **Tests that could not fail** are rewritten below (real applier with an induced failure; dev-package replay at the same version; injected timeout; signature stager with `allowDev:false`).
- **Version comparison.** Compare parsed versions with the verifier's rules (`1.2.3` equals `1.2.3.0`). The running version comes from the Host assembly informational version with `+sha` stripped.

## Units (each: failing test first, red run recorded, then the code)

### Unit 1: Manifest writer and release workflow
- `UpdateManifestWriter.Build(packageDir, version, notAfter)` in `src/Updates/src`: hashes every file except `manifest.json` and `manifest.json.sig`, writes `manifest.json` with `NotAfter`.
- `scripts/sign-package.ps1 -PackageDir -KeyFile`: the openssl command above; fails on non-zero exit or a signature that is not 64 bytes.
- `.github/workflows/release-package.yml`: `workflow_dispatch` takes a `tag` input; `environment: release`; builds `artifacts\package`; writes the manifest; signs with the secret (temp file, deleted after); verifies the signature; zips the contents with no base folder; uploads to the release for `tag`.
- Tests (`src/Updates/tests/UpdateManifestWriterTests.cs`): `WriterOutputVerifiesAfterSigning` (BouncyCastle sign, then `Ed25519PackageSignatureVerifier` and `UpdatePackageVerifier` report Valid), `WriterSkipsManifestFiles`, `WriterSetsNotAfter`.

### Unit 2: GitHub release client
- `GitHubReleaseClient(HttpClient, owner, repo)`: `GetLatestAsync(ct)` returns tag, version and the zip asset (`AiDevHelpers-package-<version>.zip`, size, URL). Skips drafts and prereleases. Null when no asset. Sets `User-Agent` and `Accept`.
- Tests (`src/Updates/tests/GitHubReleaseClientTests.cs`, fake handler): `FindsPackageZipAsset`, `NoZipAssetReturnsNull`, `RejectsTagWithoutVersion`, `SkipsPrerelease`, `SendsUserAgent`.

### Unit 3: Downloader to stager
- `UpdateDownloader(HttpClient, IUpdateStager, updatesRoot)`: checks the URL is https and an allowed host (also after redirects); streams into `updatesRoot\.download\<guid>` with `ResponseHeadersRead`; aborts above 1 GB or above the Content-Length cap; checks the byte count against the asset size; stages with `IUpdateStager`; requires the staged `Version` to equal the tag version, else deletes the staged folder; deletes the download folder either way.
- Tests (`src/Updates/tests/UpdateDownloaderTests.cs`): `DownloadStagesVerifiedPackage`, `SizeMismatchDeletesDownload`, `OversizeDownloadAbortsAtCap`, `NonHttpsUrlRejected`, `VersionMismatchNotStaged`, `ZipWithBadSignatureNotStaged` (stager built with `allowDev:false` and the real signature verifier).

### Unit 4a: Supervisor hold gate (Host)
- `McpSupervisor` gets `HoldAsync` (set flag, kill the tree, await exit) and `Release` (clear flag, start immediately, no backoff). While held, the loop does not launch and an exit does not count as a failure.
- Tests (`src/Tray/Host/tests/McpSupervisorHoldTests.cs`, fake launcher): `HeldProcessStaysDown`, `ReleaseStartsWithoutBackoff`, `HoldDoesNotCountAsFailure`.

### Unit 4: Drain
- `IUpdateDrain` with `NoOpUpdateDrain`, and `McpDrain(IMcpProcessControl, TimeSpan timeout, TimeProvider clock)`: holds MCP.Http, waits for exit up to the timeout, reports whether it stopped. `IMcpProcessControl` is implemented by the Host over the hold gate; tests use a fake.
- Tests (`src/Updates/tests/MaintenanceDrainTests.cs`): `DrainHoldsAndWaits`, `DrainTimeoutReturnsFalse` (fake clock, no real wait).

### Unit 5: Installer and startup applier
- `StagedUpdateInstaller(IUpdateDrain, IUpdateApplier, IUpdatePackageVerifier, UpdateReplayRecord, updatesRoot, installDir, backupDir)`: verifies the newest staged package; on verify failure renames it `*.rejected`; drains; applies (empty keep list); on apply failure rolls back, renames the staged folder `*.rejected`, releases the hold, and returns failure; on success records it in the replay record and returns success for Unit 6 to exit.
- `StartupUpdateApplier`: runs synchronously in `Main` before the supervisor starts; applies the newest valid staged package; `FindNewestStaged` skips `.rejected`, `.download`, `.staging` and unparseable names.
- Tests (`src/Updates/tests/StagedUpdateInstallerTests.cs`, `StartupUpdateApplierTests.cs`): `InstallAppliesStagedPackage`, `FailedApplyRollsBackAndRejects` (real applier, induced failure, install folder unchanged), `FailedApplyIsNotRetried` (folder renamed `*.rejected`), `ReplayAtSameVersionNotInstalledTwice` (dev packages allowed at the same version), `StartupAppliesNewestStaged`, `DownloadAndStagingFoldersIgnored`, `InstallNeverWritesOutsideInstallDir` (a ProgramData file is untouched).

### Unit 6: Restart seam and watcher in the Host
- After a successful apply the Host exits with code 1 through an injected `Action<int>`, so the service recovery action restarts it on the new files. Test `AppliedUpdateExitsNonZeroAfterFilesInPlace` (files are already new when the exit action runs).
- `StagedUpdateWatcher : BackgroundService` in `src/Tray/Host/src`: first check 2 minutes after start, then every 6 hours. Each check fetches the latest release; if its version is newer than the running version, downloads, stages and installs. Network errors are logged and retried next interval.
- Tests (`src/Tray/Host/tests/StagedUpdateWatcherTests.cs`, strict mocks that fail on unexpected calls): `CheckOnceInstallsNewerRelease`, `CheckOnceIgnoresSameOrOlderVersion` (downloader not called), `CheckOnceSurvivesNetworkError`.

### Unit 7: Wiring, ACL and close-out
- Host `Main`: `UpdatesFolderAcl.Apply` on the updates folder; `StartupUpdateApplier` synchronously; register the watcher. Add the ProjectReference to `AiDevHelpers.Updates`. Update the header comment.
- `src/Updates/PORTED.md`: list the 10b files with their VideoForensics source at `7e47bd9b`.
- Lite gate for touched projects; plan status; PR into `wip` once #134 has merged.

## Status
- Review: done; findings folded in above.
- Unit 1: done. Red run: `UpdateManifestWriterTests` failed to compile (no `UpdateManifestWriter`). Green: 49/49 in `src/Updates/tests`. Tool `src/Updates/tools/AiDevHelpers.Updates.ManifestTool` smoke-tested: write produces the manifest; verify rejects an unsigned package (exit 1). Workflow and `scripts/sign-package.ps1` are written but not run: the signing step needs a real run of the `release` environment.
- Unit 2: done. Red run: `GitHubReleaseClientTests` failed to compile (no `GitHubReleaseClient`). Green: 55/55 in `src/Updates/tests`.
- Unit 3: done. Red run: `UpdateDownloaderTests` failed to compile (no `UpdateDownloader`). Green: 62/62 in `src/Updates/tests`.
- Units 4a–7: not started.
