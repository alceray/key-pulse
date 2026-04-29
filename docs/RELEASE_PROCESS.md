# KeyPulse Release Process

This document is the canonical release runbook (commands and behavior details). Use `docs/RELEASE_CHECKLIST.md` as the execution checklist.

## Versioning Scheme

The git tag is the single source of truth for release versions. The workflow automatically injects versions from the tag into the build — no manual version bumps in `KeyPulse.csproj` or `installer/KeyPulse.iss` are required for GitHub releases.

- `KeyPulse.csproj` `Version` and `FileVersion` are overridden at publish time via MSBuild `/p:` args
- `installer/KeyPulse.iss` `AppVersion` is overridden at compile time via `/DAppVersion=...`

`KeyPulse.csproj` may keep a developer-default version (e.g. `1.1.0`) for local builds. It does not need to be bumped before tagging.

## Automated Release (GitHub Actions)

Pushing a version tag triggers the release workflow (`.github/workflows/release.yml`):

1. Extracts the version from the pushed tag (e.g. `v1.2.0` → `1.2.0`)
2. Publishes the app with the tag version injected (`/p:Version=...`, `/p:FileVersion=...`)
3. Compiles the installer with the tag version injected (`/DAppVersion=...`)
4. Creates a GitHub Release with the installer attached and `CHANGELOG.md` as release notes

## How to Cut a Release

1. Add an entry to `CHANGELOG.md`.
2. Commit and push.
3. Push a version tag with the helper script:
   ```powershell
   .\scripts\New-Release.ps1 -Version "1.2.0"
   ```
4. The script validates a clean working tree and prevents duplicate tags.
5. GitHub Actions builds and publishes the release automatically.

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

KeyPulse updates are installer-driven:

1. Download the latest installer from the GitHub Release.
2. Run it over the existing install — do not uninstall first.
3. Installer upgrades in place (`AppId` is unchanged).
4. User data in `%AppData%\KeyPulse` is preserved unless explicitly removed during uninstall.

## Verification

- App launches and reports expected version.
- Upgrade preserves:
  - `%AppData%\KeyPulse\keypulse-data.db`
  - `%AppData%\KeyPulse\settings.json`
  - `%AppData%\KeyPulse\Logs\`
- Installer filename includes the release version.
