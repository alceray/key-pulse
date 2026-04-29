# KeyPulse

KeyPulse is a Windows-only WPF app for tracking USB keyboards and mice.
It monitors connection/disconnection events, keeps a per-device event history,
captures minute-level raw input activity, and stores everything in a local SQLite database.

## Supported Operating System

- Windows only
- Not supported on macOS or Linux

KeyPulse targets `.NET 8` with `net8.0-windows` and depends on Windows-specific technologies including WPF,
WMI (`System.Management`), Windows Raw Input (`WM_INPUT`), and the WinForms tray icon API.

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
- Includes a dashboard with:
  - connected-device and top-usage summary cards,
  - keyboard/mouse usage pie charts by selected time range,
  - an input-activity timeline with bucket-size and smoothing controls.
- Provides a device list UI with rename support and an event log view.
- Supports tray/background startup in production builds.

## Tech Stack

- .NET `net8.0-windows`
- WPF
- Entity Framework Core + SQLite
- `System.Management` (WMI watchers)
- Windows Raw Input (`WM_INPUT`)
- Dependency Injection (`Microsoft.Extensions.DependencyInjection`)

## Runtime Behavior Worth Knowing

- Single-instance app: launching a second instance signals the existing instance to restore/focus its main window.
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
- Dashboard activity timeline behavior:
  - bucket values are forced to zero while the app was not running,
  - keyboard series = `Keystrokes`, mouse series = `MouseClicks + MouseMovementSeconds`,
  - X-axis labels switch by range: `MM-dd HH:mm` (< 7 days), `MM-dd` (>= 7 days), `yyyy-MM` (>= 1 year).
- Database file path:
  - `%AppData%\\KeyPulse\\keypulse-data.db`

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

Startup mode defaults by build configuration:

- `Debug`: opens main window on launch (foreground mode).
- `Release`: starts with tray icon (background mode).

Launch argument override:

- `--startup`
  - forces tray/background startup for that launch (useful for startup entries and installer shortcuts).

## Troubleshooting

- Launching a second instance does not open a new window:
  - expected behavior; KeyPulse is single-instance and should restore/focus the running instance.
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

Common build/run commands:

- `dotnet restore`
  - Restores NuGet dependencies.
- `dotnet build -c Debug`
  - Builds Debug output.
- `dotnet build -c Release`
  - Builds Release output.
- `dotnet run -c Debug`
  - Runs with Debug defaults (foreground window).
- `dotnet run -c Release`
  - Runs with Release defaults (tray/background).
- `dotnet clean`
  - Cleans build artifacts.

## Release

For release packaging (for example with Inno Setup), publish app output first:

- `dotnet publish -c Release -r win-x64 --no-self-contained -o publish\`
  - Produces release binaries under `publish\` for installer input.
- `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\`
  - Produces a self-contained publish output (larger, runtime included).

If Inno Setup is installed, compile installer from script:

- `iscc "installer\KeyPulse.iss"`
  - Builds installer output under `installer\output\`.
- In Inno Setup Compiler app:
  - Open `installer\KeyPulse.iss`
  - Click **Build** (or press `F9`) to compile the installer.

Upgrade flow (installed app already present):

- Bump `#define AppVersion` in `installer\KeyPulse.iss`.
- Re-run publish and installer build commands.
- Run the new installer **without uninstalling first**; Inno Setup upgrades in place when `AppId` is unchanged.
- Verify app history/settings remain under `%AppData%\KeyPulse\`.

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

