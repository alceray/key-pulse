# KeyPulse Signal Release Process

This document is the canonical release runbook (commands and behavior details). Use
`docs/RELEASE_CHECKLIST.md` as the execution checklist.

## Versioning Scheme

The git tag is the single source of truth for release versions. The workflow automatically injects versions from the tag into the build — no manual version bumps in
`KeyPulse.csproj` or `installer/KeyPulse.iss` are required for GitHub releases.

- `KeyPulse.csproj` `Version` and `FileVersion` are overridden at publish time via MSBuild `/p:` args
- `installer/KeyPulse.iss` `AppVersion` is overridden at compile time via `/DAppVersion=...`

`KeyPulse.csproj` may keep a developer-default version (e.g.
`1.2.0`) for local builds. It does not need to be bumped before tagging, though keeping it in sync
with the latest tag is good hygiene.

## Automated Release (GitHub Actions)

Pushing a version tag triggers the release workflow (`.github/workflows/release.yml`):

1. Extracts the version from the pushed tag (e.g. `v1.2.0` → `1.2.0`)
2. Publishes the app with the tag version injected (`/p:Version=...`, `/p:FileVersion=...`)
3. Compiles the installer with the tag version injected (`/DAppVersion=...`)
4. Extracts the matching `## [<version>]` section from `CHANGELOG.md` (via the workflow's
   `Extract release notes from changelog` step) and creates a GitHub Release with the installer
   attached and that section as the release notes — not the whole file

## How to Cut a Release

1. Choose the next version (semver): bump the **minor** when the release adds features
   (an `### Added` section), the **patch** for fixes-only releases, and the **major** for breaking
   changes. The changelog's section headings are the quickest tell — any `### Added` entries mean a
   minor, not a patch.
2. Add the matching `## [<version>] - <date>` entry to `CHANGELOG.md`, and bump `Version` /
   `FileVersion` in `KeyPulse.csproj` for the new release.
3. Commit and push.
4. Push a version tag with the helper script:
   ```powershell
   .\scripts\New-Release.ps1
   ```
   The script uses `Version` from `KeyPulse.csproj`. Pass `-Version "1.2.0"` to override it.
5. The script validates a clean working tree and asks you to confirm the release version before
   tagging or pushing. Enter `y` or `yes` to continue; Enter alone cancels. If the tag already exists
   locally, the confirmation explicitly asks to replace it locally and on origin.
6. GitHub Actions builds and publishes the release automatically.

Manual fallback:

```powershell
git tag v1.2.0
git push origin v1.2.0
```

## Manual Build (local testing)

```powershell
.\scripts\Build-Release.ps1 -Version "1.2.0"
```

Omit `-Version` to use the default version in `KeyPulse.csproj`.

## Update Strategy

KeyPulse Signal updates are installer-driven:

1. Download the latest installer from the GitHub Release.
2. Run it over the existing install — do not uninstall first.
3. Installer upgrades in place (`AppId` is unchanged).
4. User data in `%AppData%\KeyPulse Signal` is preserved unless explicitly removed during uninstall.

## Verification

- App launches and reports expected version.
- Upgrade preserves:
  - `%AppData%\KeyPulse Signal\keypulse-data.db`
  - `%AppData%\KeyPulse Signal\settings.json`
  - `%AppData%\KeyPulse Signal\Logs\`
- Installer filename includes the release version.

## Deferred Code Signing

Code-signing rollout remains deferred. The signing script exists, but the current GitHub Actions release workflow does not invoke it. This is the remaining follow-up from the completed production-readiness plan.

- **Available:** `scripts/Sign-ReleaseArtifacts.ps1` supports a certificate thumbprint (`KEYPULSE_SIGN_CERT_THUMBPRINT`) or PFX (`KEYPULSE_SIGN_PFX_PATH` and `KEYPULSE_SIGN_PFX_PASSWORD`), SHA-256 digests, and RFC3161 timestamps.
- **Remaining:** Obtain a signing certificate and configure access to it in the release environment.
- **Remaining:** Wire signing into the release workflow: sign the published app before compiling the installer, then sign the installer before generating its checksum and uploading release artifacts.
- **Remaining:** Make signing failures fail the release and verify signatures on both the installed executable and final installer.
- **Completion criteria:** A release produced through the normal workflow has valid, timestamped signatures on both artifacts, with verification recorded in the release checklist.
