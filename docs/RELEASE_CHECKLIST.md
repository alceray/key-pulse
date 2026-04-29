# KeyPulse Release Checklist

## Pre-Release

- [ ] Add release notes entry in `CHANGELOG.md`.
- [ ] Ensure no pending migrations unless intentionally included.
- [ ] Run smoke tests in Debug and Release.

## Release

```powershell
git tag v1.x.x
git push origin v1.x.x
```

GitHub Actions will automatically:
- Inject version from the tag into the app and installer
- Restore, build, and publish the app
- Compile the installer with the correct version
- Create a GitHub Release with the installer and changelog attached

- [ ] Confirm GitHub Actions workflow completed successfully.
- [ ] Confirm installer is attached to the GitHub Release.
- [ ] Confirm installer filename includes version.

## Manual Build (local fallback only)

```powershell
Set-Location "D:\Projects\visual-studio\key-pulse"
dotnet restore
dotnet publish -c Release -r win-x64 --no-self-contained -o publish\
iscc "installer\KeyPulse.iss"
```

## Optional Signing

```powershell
.\scripts\Sign-ReleaseArtifacts.ps1
```

- [ ] Verify signature if signing is enabled.

## Upgrade Validation (Installer-Driven)

- [ ] Install previous released version.
- [ ] Generate sample activity/settings.
- [ ] Run new installer over existing install.
- [ ] Confirm app starts successfully.
- [ ] Confirm old data remains available.

