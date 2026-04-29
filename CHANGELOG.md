# Changelog

All notable changes to this project are documented in this file.

## [1.1.0] - 2026-04-29

### Added

- Release process documentation (`docs/RELEASE_PROCESS.md`, `docs/RELEASE_CHECKLIST.md`).
- Release automation scripts (`scripts/New-Release.ps1`, `scripts/Build-Release.ps1`).
- Installer-driven update guidance for small trusted distribution.
- Signing script installer artifact auto-discovery.
- Pre-migration database backups for safer schema upgrades.
- Code signing support for release artifacts via `scripts/Sign-ReleaseArtifacts.ps1`.
- GitHub Actions workflow for automated tag-driven release versioning, building, and publishing.
- Single-instance enforcement per build mode (separate Debug and Release instances).
- Debug log level configuration for Release builds.
- Launch on Login default setting (`AppUserSettings`).

### Changed

- Renamed project and solution from "KeyPulse" to "KeyPulse Signal" for improved brand clarity.
- Separated test data path from release data (`%AppData%\KeyPulse\Test` vs `%AppData%\KeyPulse`).
- Centralized version metadata in `KeyPulse.csproj`.
- Inno Setup now derives installer version from published executable version at build time.

## [1.0.0] - 2026-04-28

### Added

- Initial production-readiness implementation baseline.

