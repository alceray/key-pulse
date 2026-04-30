# KeyPulse Signal

KeyPulse Signal is a Windows-only WPF app for tracking USB keyboards and mice.
It monitors connection/disconnection events, keeps a per-device event history,
captures minute-level raw input activity, and stores everything in a local SQLite database.

## Supported Operating System

- Windows only
- Not supported on macOS or Linux

KeyPulse Signal targets `.NET 8` with `net8.0-windows` and depends on Windows-specific technologies including WPF,
WMI (`System.Management`), Windows Raw Input (`WM_INPUT`), and the WinForms tray icon API.

## Core Functionality

- Detects USB keyboard and mouse devices connected to the system at startup.
- Monitors device insertion and removal events via WMI.
- Maintains per-device session duration tracking for active connections.
- Derives total usage metrics from persisted connection event history.
- Records minute-level activity snapshots per device, including:
  - keystroke counts,
  - mouse click counts,
  - mouse movement active seconds (`0`-`60`).
- Provides real-time visual indicators for active input (key/button hold state).
- Implements a comprehensive dashboard featuring:
  - connected-device and top-usage summary cards,
  - keyboard/mouse usage distribution charts with configurable time ranges,
  - activity timeline visualization with adjustable bucket size and smoothing parameters.
- Offers a device management interface with support for device renaming and event history inspection.
- Supports background/tray startup mode for production deployments.

## Tech Stack

- .NET `net8.0-windows`
- WPF
- Entity Framework Core + SQLite
- `System.Management` (WMI watchers)
- Windows Raw Input (`WM_INPUT`)
- Dependency Injection (`Microsoft.Extensions.DependencyInjection`)

## Documentation

- `AGENTS.md`
  - architecture guide and implementation conventions for the codebase
- `docs/PRODUCTION_READINESS_PLAN.md`
  - tracked production hardening and release-readiness plan
- `docs/RELEASE_PROCESS.md`
  - versioning, packaging, and release workflow
- `docs/RELEASE_CHECKLIST.md`
  - step-by-step release validation checklist
- `CHANGELOG.md`
  - notable release-to-release changes

## Runtime Behavior

- **Single-instance enforcement**: Only one instance per build configuration is permitted to run; launching a second instance in the same mode will signal the existing instance to activate and return focus.
- **Initialization sequence**:
  - Executes database migrations,
  - Performs crash recovery for unclean shutdowns if necessary,
  - Rebuilds persisted device usage snapshots,
  - Loads persisted devices and lifecycle events,
  - Catalogs currently connected HID-compliant devices,
  - Initializes WMI event watchers for device state changes,
  - Begins capturing per-device raw input activity.
- **Usage metrics**: `TotalUsage` is computed from connection events and continuously increments while a device maintains an active session.
- **Connection state**: `IsConnected` reflects the current connection state and is used for device list filtering.
- **Activity state**: `IsActive` is a transient flag distinct from connection state, driven by momentary raw input (key/button hold) and used for activity highlighting only.
- **Activity snapshots**: Input activity is persisted at minute-level granularity per device in the `ActivitySnapshots` table.
- **Dashboard timeline semantics**:
  - Activity buckets reflect zero contribution during periods when the application was not running,
  - keyboard activity displays keystroke counts; mouse activity displays click counts plus movement-active seconds,
  - X-axis timestamp format adapts to the selected time range: `MM-dd HH:mm` (< 7 days), `MM-dd` (≥ 7 days and < 365 days), `yyyy-MM` (≥ 365 days).
- **Logging**: Application logs are written to rolling daily files under `%AppData%\KeyPulse\Logs` (Release) or `%AppData%\KeyPulse\Test\Logs` (Debug/testing).
- **Service lifecycle**: Singleton services are disposed through the dependency injection container during graceful application termination; `Dispose()` methods implement idempotency and log redundant invocations at debug level.
- **Data persistence**:
  - `Release`: `%AppData%\KeyPulse\keypulse-data.db`
  - `Debug` / testing: `%AppData%\KeyPulse\Test\keypulse-data.db`

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
  - expected behavior; KeyPulse Signal is single-instance and should restore/focus the running instance.
- Build fails because `KeyPulse Signal.exe` is locked:
  - stop the running app before rebuilding Debug output.
- Device name appears as `Unknown Device`:
  - Windows did not provide a friendly name for that device ID at lookup time; the app tries native SetupAPI/cfgmgr32 lookup first and only falls back to PowerShell if native lookup returns nothing.
- Duplicate event warnings in debug output:
  - WMI can emit multiple insert callbacks for one physical USB connect; the app aggregates bursty callbacks into one logical connection and the database uniqueness constraint remains a secondary safeguard.
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

Release/versioning details live in:

- `docs/RELEASE_PROCESS.md`
- `docs/RELEASE_CHECKLIST.md`
- `CHANGELOG.md`

GitHub releases are tag-driven. Push a
`v*.*.*` tag and the workflow handles versioning, building, and publishing automatically. See `docs/RELEASE_PROCESS.md`.

For installed users, updates are installer-driven: run the latest installer over the existing install.

## Architecture Notes

- `UsbMonitorService`
  - handles WMI device detection,
  - aggregates bursty insert callbacks before emitting one logical `Connected` event,
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
  - rebuilds persisted usage snapshots on startup,
  - relies on `HeartbeatFile` to estimate the approximate end time of an unclean previous session.
