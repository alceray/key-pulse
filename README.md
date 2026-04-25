# KeyPulse

KeyPulse is a Windows WPF app for tracking USB keyboards and mice.
It monitors connection/disconnection events, keeps a per-device event history,
captures minute-level raw input activity, and stores everything in a local SQLite database.

## What It Does

- Detects USB keyboard/mouse devices currently connected to the system at app startup.
- Watches live insert/remove events using WMI.
- Tracks per-device current session time while a device is connected.
- Calculates `Total Usage` from persisted connection event history.
- Captures per-device minute buckets of keyboard/mouse activity:
  - keystroke counts,
  - mouse click counts,
  - mouse movement active seconds (`0`-`60`).
- Highlights device icons when keys or mouse buttons are currently held.
- Provides a device list UI with rename support and an event log view.
- Supports optional tray/background mode via app settings.

## Tech Stack

- .NET `net8.0-windows`
- WPF
- Entity Framework Core + SQLite
- `System.Management` (WMI watchers)
- Windows Raw Input (`WM_INPUT`)
- Dependency Injection (`Microsoft.Extensions.DependencyInjection`)

## Runtime Behavior Worth Knowing

- Single-instance app: launching a second instance shows "already running".
- Startup flow:
  - runs database migrations,
  - recovers from an unclean previous shutdown if needed,
  - rebuilds persisted device usage snapshots,
  - loads persisted devices/events,
  - snapshots currently connected HID devices,
  - starts WMI watchers for live changes,
  - starts Raw Input capture for live per-device activity.
- `TotalUsage` is derived from connection events and ticks live while a device is connected.
- `IsConnected` is the current connection state used for filtering the default device list.
- `IsActive` is separate from connection state:
  - it is a transient live flag driven by raw keyboard/mouse hold state,
  - it is used for activity highlighting, not persisted connection state.
- Activity snapshots are stored per device per minute in `ActivitySnapshots`.
- Database file path:
  - `%AppData%\\KeyPulse\\devices.db`

## Data Model

KeyPulse persists three main record types:

- `Devices`
  - mutable snapshot for fast UI reads,
  - stores metadata like `DeviceName`, `DeviceType`, `LastConnectedAt`, and persisted `TotalUsage`.
- `DeviceEvents`
  - immutable lifecycle log,
  - stores `AppStarted`, `AppEnded`, `ConnectionStarted`, `ConnectionEnded`, `Connected`, and `Disconnected`.
- `ActivitySnapshots`
  - immutable minute buckets,
  - stores `Keystrokes`, `MouseClicks`, and `MouseActiveSeconds` for a `DeviceId` + minute.

## Configuration

`App.config`:

- `RunInBackground` (`true`/`false`)
  - `false`: opens main window on launch.
  - `true`: starts with tray icon; open UI from tray menu or left-click tray icon.

Environment variable override:

- `KEYPULSE_RUN_IN_BACKGROUND`
  - if set to `true`, forces background/tray startup.

## Troubleshooting

- App says it is already running:
  - close the existing KeyPulse process and relaunch.
- Build fails because `KeyPulse.exe` is locked:
  - stop the running app before rebuilding Debug output.
- Device name appears as `Unknown Device`:
  - Windows did not provide a friendly name for that device ID at lookup time.
- Duplicate event warnings in debug output:
  - expected in some cases; duplicate event inserts are skipped safely.
- Devices appear stuck as connected after a crash or forced stop:
  - the next startup runs crash recovery and backfills missing `AppEnded` / `ConnectionEnded` events.

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

## Architecture Notes

- `UsbMonitorService`
  - handles WMI device detection,
  - manages in-memory `DeviceList` and `DeviceEventList`,
  - writes lifecycle events and updates device connection snapshots.
- `RawInputService`
  - registers for background raw input,
  - maps raw device handles back to KeyPulse `DeviceId` values,
  - aggregates minute-level activity before flushing to the database.
- `DataService`
  - owns database access,
  - runs migrations,
  - performs crash recovery,
  - rebuilds persisted usage snapshots on startup.

