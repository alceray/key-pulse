# KeyPulse

KeyPulse is a Windows WPF app that tracks USB keyboard and mouse usage.
It monitors connection/disconnection events, shows currently connected devices,
and records usage history in a local SQLite database.

## What It Does

- Detects USB keyboard/mouse devices currently connected to the system at app startup.
- Watches live insert/remove events using WMI.
- Tracks per-device `Current Session` time while connected.
- Calculates `Total Usage` from persisted connection event history.
- Provides a device list UI with rename support and an event log view.
- Supports optional tray/background mode via app settings.

## Tech Stack

- .NET `net8.0-windows`
- WPF
- Entity Framework Core + SQLite
- `System.Management` (WMI watchers)
- Dependency Injection (`Microsoft.Extensions.DependencyInjection`)

## Runtime Behavior Worth Knowing

- Single-instance app: launching a second instance shows "already running".
- Startup flow:
  - loads persisted devices/events,
  - snapshots currently connected HID devices,
  - starts WMI watchers for live changes.
- `CurrentSessionUsage` is runtime-only and starts from in-app session begin/end signals.
  - This avoids stale timing when previous app shutdown was unclean.
- `IsActive` is still used as the current connection state for UI filtering and persistence.
- Database file path:
  - `%AppData%\\KeyPulse\\devices.db`

## Configuration

`App.config`:

- `RunInBackground` (`true`/`false`)
  - `false`: opens main window on launch.
  - `true`: starts with tray icon; open UI from tray menu or left-click tray icon.

## Troubleshooting

- App says it is already running:
  - close the existing KeyPulse process and relaunch.
- Build fails because `KeyPulse.exe` is locked:
  - stop the running app before rebuilding Debug output.
- Device name appears as `Unknown Device`:
  - Windows did not provide a friendly name for that device ID at lookup time.
- Duplicate event warnings in debug output:
  - expected in some cases; duplicate event inserts are skipped safely.

## Developer Notes

In Developer PowerShell, use EF Core commands:

- `dotnet ef migrations add <MigrationName>`
  - Creates a new migration under `Migrations` from current model state.
- `dotnet ef migrations remove`
  - Removes the last unapplied migration.
- `dotnet ef database update`
  - Applies pending migrations.
- `dotnet ef database update <MigrationName>`
  - Migrates database to a specific target migration.
