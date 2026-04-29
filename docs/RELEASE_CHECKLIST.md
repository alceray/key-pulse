# KeyPulse Release Checklist

## Pre-Release

- [ ] Add release notes entry in `CHANGELOG.md`.
- [ ] Ensure no pending migrations unless intentionally included.
- [ ] Run smoke tests in Debug and Release.

## Release

- [ ] Run the release command from `docs/RELEASE_PROCESS.md` -> `How to Cut a Release`.
- [ ] Confirm GitHub Actions workflow completed successfully.
- [ ] Confirm installer is attached to the GitHub Release.
- [ ] Confirm installer filename includes version.

## Optional Signing

- [ ] If signing is enabled, run `.\scripts\Sign-ReleaseArtifacts.ps1`.
- [ ] Verify signature.

## Upgrade Validation (Installer-Driven)

- [ ] Install previous released version.
- [ ] Generate sample activity/settings.
- [ ] Run new installer over existing install.
- [ ] Confirm app starts successfully.
- [ ] Confirm old data remains available.

