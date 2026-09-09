# Changelog

All notable changes to this project are documented in this file.

## [1.4.0] - 2026-09-08

### Added

- Optional PostgreSQL storage.
- History transfers between SQLite and PostgreSQL, and between PostgreSQL databases.
- Dark mode.
- **Save and restart** for database changes, with transfer progress.

### Changed

- Simplified storage settings and moved retention into General.
- New settings default to keeping 24 months of minute-level activity.
- Automatic SQLite backup cleanup keeps the three newest backups.

### Removed

- Duplicate hidden-device settings panel.

### Fixed

- Incorrect activity timestamps during the repeated hour when daylight saving time ends.

## [1.3.2] - 2026-07-16

### Fixed

- Tray app shutting down when the update prompt is closed.
- Notifications not refreshing for newer updates.

## [1.3.1] - 2026-07-14
> ⚠️ Automatic updating is broken in this version.                                                                                                                                                                                                                                                         
> Download and run a v1.3.2+ installer over your current installation. Do not uninstall first - settings and activity history will be preserved.

### Added

- Manual device type correction from the device list.
- Suggestions when a device may have the wrong type.

## [1.3.0] - 2026-06-19
> ⚠️ Automatic updating is broken in this version.                                                                                                                                                                                                                                                         
> Download and run a v1.3.2+ installer over your current installation. Do not uninstall first - settings and activity history will be preserved.

### Added

- "Days Connected" column in the device list.
- Pause and resume tracking from the dashboard or tray menu.
- "Close to tray" setting with a one-time reminder.
- Retention setting for minute-level activity.

### Changed

- Simplified device list columns and sorting.
- Active time now updates live to the second and shows a share of connected time.
- Total connected time now includes seconds.
- Restyled calendar tiles and standardized connection and activity colors.
- Moved hidden-device controls into Settings.
- Faster loading of long activity ranges and lifetime totals.
- Renamed the `--startup` launch argument to `--tray`.

### Fixed

- Incorrect session counts for connections spanning multiple days.
- Conflicts between devices sharing an identifier.
- Incorrect first-launch detection.

## [1.2.1] - 2026-06-05

### Added

- Automatic updates with confirmation and restart to the tray.
- A setting to turn off automatic update prompts.
- Installer checksum verification before updates run.

## [1.2.0] - 2026-06-01

### Added

- Calendar with daily device summaries and hourly activity.
- Device selection to highlight activity across dashboard charts.
- Live input counters for each device.
- Pan and zoom on the activity chart.
- Hide devices from the dashboard and calendar while keeping tracking active.
- Progress bars for time-based metrics.

### Changed

- Consistent device colors and an "Others" group for small pie-chart slices.
- Connection indicators and connected-first sorting in the device list.
- Chart refreshes preserve pan and zoom.
- Reduced installed size from about 105 MB to 15 MB.
- Automatic chart resolution and smoothing settings.

### Fixed

- App hanging during Windows shutdown.
- Incorrect daily connection statistics.
- Missing tooltips on today's activity bars.

## [1.1.1] - 2026-04-30

### Added

- Troubleshooting page with searchable logs and live updates.
- Update-check settings and tray notifications.

### Changed

- Clearer diagnostic logs.
- The main window now opens on first launch.
- More reliable refresh timing across views.
- Consistent application icons in the taskbar and tray.
- Renamed "Total Usage" to "Connected Time" in the UI.

### Fixed

- Dashboard refreshes stopping after tab switches.
- Duplicate connection events.

## [1.1.0] - 2026-04-29

### Added

- One running instance per build mode.
- Launch on Login setting.
- Automatic backups before database migrations.
- Improved startup and installer reliability.

### Changed

- Renamed the app to "KeyPulse Signal".
- Separated Debug and Release data.

## [1.0.0] - 2026-04-28

### Added

- Initial stable release.
