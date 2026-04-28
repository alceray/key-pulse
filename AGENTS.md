# AGENTS.md: KeyPulse Architecture Guide

## Quick Overview

**KeyPulse** is a .NET 8 WPF desktop app for tracking USB keyboard and mouse connections on Windows and recording
per-device input activity. It uses WMI event watchers for device arrival/removal, Windows Raw Input for per-device
keyboard/mouse activity, and SQLite+EF Core for persistence. The app is single-instance and supports optional
tray/background mode.

**Key Tech Stack**: WPF, EF Core 9, SQLite, System.Management (WMI), Windows Raw Input (`WM_INPUT`), Dependency
Injection

---

## Architecture & Data Flow

### Major Components

1. **UsbMonitorService** (Singleton)
    - Monitors USB device insertion/removal via WMI `__InstanceCreationEvent` and `__InstanceDeletionEvent`
    - Maintains `DeviceList` and `DeviceEventList` as `ObservableCollection<T>` instances for UI binding
    - Screens for HID keyboard/mouse devices only (`kbdhid`, `mouhid` services)
    - Writes app/device lifecycle events and keeps the in-memory device snapshot in sync
    - Owns the heartbeat timer used by crash recovery
    - See: `Services/UsbMonitorService.cs`

2. **RawInputService** (Singleton)
    - Registers hidden `WM_INPUT` listeners using `RIDEV_INPUTSINK`, so activity is captured even in tray/background
      mode
    - Maps raw input handles back to KeyPulse `DeviceId` values
    - Tracks per-device keystrokes, mouse clicks, and mouse-movement-active seconds in one-minute buckets
    - Flushes completed minute buckets to `ActivitySnapshots` every minute and flushes the current partial minute on
      shutdown
    - Raises `ActivityStateChanged` so the UI can highlight currently active devices
    - See: `Services/RawInputService.cs`

3. **DataService** (Singleton)
    - Single source for all database operations
    - Runs migrations and enables SQLite WAL mode on startup
    - Crash recovery: detects unclean shutdowns and writes missing `AppEnded`/`ConnectionEnded` events
    - Rebuilds persisted device usage snapshots from the event log on startup
    - Persists and queries minute-level `ActivitySnapshot` rows
    - See: `Services/DataService.cs`

4. **ApplicationDbContext** (`DbContext`)
    - Three tables: `Devices` (mutable snapshot), `DeviceEvents` (immutable lifecycle log), and `ActivitySnapshots` (
      minute-level input activity)
    - Database stored at `%AppData%\KeyPulse\devices.db`
    - Unique constraint on `DeviceEvents(DeviceId, EventTime, EventType)` prevents duplicate lifecycle events
    - Unique constraint on `ActivitySnapshots(DeviceId, Minute)` prevents duplicate minute buckets
    - See: `Data/ApplicationDbContext.cs`

### Data Persistence Model

- **DeviceEvents** = immutable, append-only log of app/device lifecycle transitions (source of truth for connection
  history)
- **Devices** = mutable, fast-read snapshot used by the UI (`DeviceName`, `DeviceType`, `LastConnectedAt`, stored
  `TotalUsage`)
- **ActivitySnapshots** = immutable minute buckets storing `Keystrokes`, `MouseClicks`, and `MouseActiveSeconds`
- Updates flow in two directions:
    - lifecycle changes append to `DeviceEvents` and update the corresponding `Device` snapshot
    - raw input activity accumulates in memory, then flushes to `ActivitySnapshots`

### Startup Sequence (See `App.OnStartup`, `UsbMonitorService`, and `RawInputService`)

1. Register unhandled-exception cleanup hooks.
2. Mutex check (single-instance enforcement). If another instance exists, signal it to restore/focus and exit.
3. Resolve startup mode from build configuration (Debug foreground, Release tray), with launch args able to force tray
   mode.
4. Build the DI container.
5. Resolve `UsbMonitorService` (which also resolves `DataService`). During construction:
    - database migrations run,
    - SQLite WAL mode is enabled,
    - `DataService.RecoverFromCrash()` backfills missing close events if needed,
    - `DataService.RebuildDeviceSnapshots()` recomputes persisted `TotalUsage` and clears stale `SessionStartedAt`,
    - historical `Devices` / `DeviceEvents` are loaded,
    - the heartbeat timer starts.
6. Show the main window immediately or initialize the tray icon, depending on background mode.
7. Await `UsbMonitorService.StartAsync()`:
    - `SetCurrentDevicesFromSystem()` writes `AppStarted`, snapshots currently connected HID devices, and emits
      `ConnectionStarted` for each one,
    - then WMI watchers are started for live insert/remove events.
8. Resolve `RawInputService` and call `Start()` to create the hidden message-only window and begin receiving `WM_INPUT`.

---

## Critical Patterns & Conventions

### Event Types & Lifecycle

Event types are defined in `Models/DeviceEvent.cs` and categorized by `EventTypeExtensions`:

- **Opening**: `ConnectionStarted`, `Connected`
- **Closing**: `ConnectionEnded`, `Disconnected`
- **App-level**: `AppStarted`, `AppEnded`

Device state management is centralized in `UsbMonitorService.AddDeviceEvent()`:

- opening events set `SessionStartedAt`
- closing events commit elapsed time into stored usage and clear `SessionStartedAt`
- app-level events are logged without touching per-device state

### Device Identification

- **Format**: `USB\VID_xxxx&PID_xxxx` (parsed from WMI `DeviceID`)
- **Classification**: `UsbDeviceClassifier.GetInterfaceSignal()` probes WMI for `Service`, `ClassGuid`, `PNPClass`
- **Type Resolution**: Multiple HID interfaces expected per device; wait for ≥2 signals before determining type (
  keyboard vs. mouse)

### Property Binding & Threading

- **ObservableObject** base class (in `Helpers/`) wraps `INotifyPropertyChanged`
- Automatically marshals property changes to UI thread via `Application.Current.Dispatcher`
- See `Models/Device.cs` for example: `SessionStartedAt` triggers dependent notifications for `IsConnected` and
  `TotalUsage`
- **Important**: `IsConnected` and `IsActive` are different concepts:
    - `IsConnected` means the device currently has an open connection session (`SessionStartedAt.HasValue`)
    - `IsActive` is a transient raw-input hold-state flag used to highlight devices while keys/buttons are currently
      held
- **Important**: `TotalUsage` is computed while connected; it displays the stored value plus elapsed time since
  `SessionStartedAt`

### Raw Input Activity Semantics

- `RawInputService` creates one in-memory bucket per `(DeviceId, Minute)`.
- **Keyboard**: every key-down increments `Keystrokes`; key-up only updates hold state.
- **Mouse buttons**: every button-down increments `MouseClicks`; button-up only updates hold state.
- **Mouse movement**: movement is recorded as a set of second-of-minute values, then persisted as `MouseActiveSeconds` (
  0–60).
- Completed minutes flush every 60 seconds; dispose/shutdown flushes the current partial minute as well.

### Dashboard Behavior (Usage + Activity)

- Dashboard top cards show current connected count and top-3 keyboard/mouse device usage summaries.
- Time-range filter supports `1 Day`, `1 Week`, `1 Month`, `1 Year`, and `All Time`.
- Activity chart supports configurable `bucketMinutes` and trailing moving-average `smoothingWindow` (both normalized
  to >= 1).
- Activity chart zeroes buckets where no app-running interval overlaps (`AppStarted`/`AppEnded` reconstruction).
- Activity chart series use:
    - keyboard = `Keystrokes`
    - mouse = `MouseClicks + MouseMovementSeconds`
- X-axis label format adapts by selected range:
    - `< 7 days`: `MM-dd HH:mm`
    - `>= 7 days and < 365 days`: `MM-dd`
    - `>= 365 days`: `yyyy-MM`
- Pie charts use hover tracking metadata (status, usage, share, connection time text) and update dashboard hover-preview
  state via tracker events.

### Duplicate Detection

- WMI fires multiple insert events (~2-3) per physical USB connection
- `_cachedDevices` de-duplicates by aggregating keyboard/mouse interface signals inside a short time window
- Only then a single `Connected` event is recorded
- `DeviceEvents` unique constraint prevents DB duplicates even if code fails

---

## Workflows & Commands

### Database Migrations (via EF Core CLI in Developer PowerShell)

```powershell
# Add new migration from current model state
dotnet ef migrations add MyMigrationName

# Remove last unapplied migration
dotnet ef migrations remove

# Apply pending migrations to DB
dotnet ef database update

# Revert to specific migration target
dotnet ef database update SomeOlderMigrationName
```

### Configuration

- **Build mode default**: Debug launches windowed; Release launches to tray/background
- **Launch args**: `--startup` forces tray/background startup for that process
- Tray icon created if background mode enabled; main window created on-demand or at startup

### Build & Run

- Target: `.net8.0-windows`, WPF enabled
- Debug builds auto-generate WPF XAML behind the scenes
- **Build may fail** if KeyPulse.exe is locked — stop running instance first
- **Assets**: `Assets/keyboard_mouse_icon.ico` copied to build output

---

## File Organization & Responsibilities

| Folder        | Purpose                                                                                                          |
|---------------|------------------------------------------------------------------------------------------------------------------|
| `Helpers/`    | `ObservableObject`, `RelayCommand`, `UsbDeviceClassifier`, `TimeFormatter`, `PowerShellScripts`, `HeartbeatFile` |
| `Services/`   | `UsbMonitorService`, `RawInputService`, `DataService` — monitoring, activity capture, persistence                |
| `Models/`     | `Device`, `DeviceEvent`, `ActivitySnapshot` + enums/extensions                                                   |
| `Data/`       | `ApplicationDbContext`, database initialization                                                                  |
| `ViewModels/` | MVVM viewmodels for each UI view (e.g., `DeviceListViewModel`)                                                   |
| `Views/`      | XAML + code-behind for UI (e.g., `DeviceListView.xaml`)                                                          |
| `Migrations/` | EF Core snapshot migrations (read-only; auto-generated)                                                          |

---

## Crash Recovery & Consistency

**Problem**: If app is force-killed (IDE stop, crash), devices may appear "stuck" as active in the DB.

**Solution** (`DataService.RecoverFromCrash`):

1. On startup, check if last app-lifecycle event was `AppStarted` (unmatched)
2. If so, retroactively add `AppEnded` and `ConnectionEnded` for orphaned devices
3. Event log stays consistent for future usage calculations

**Snapshot Rebuild** (`DataService.RebuildDeviceSnapshots`):

- Recomputes persisted `TotalUsage` from the event log
- Clears runtime-only `SessionStartedAt` so devices do not appear connected after an unclean shutdown
- Called at startup after recovery

---

## Important Implementation Details

### Duplicate Event Prevention

- `DeviceInsertedEvent` accumulates keyboard/mouse signals until ≥2 are seen within a short timeframe
- Only emits one `Connected` event per physical device insertion
- DB unique constraint `(DeviceId, EventTime, EventType)` is secondary safeguard

### CurrentSessionUsage ("Session" vs. "Total")

- **SessionStartedAt**: Set when device becomes active (opening event), cleared when inactive
- **IsConnected**: Computed from `SessionStartedAt.HasValue`
- **IsActive**: Separate transient hold-state driven by `RawInputService.ActivityStateChanged`
- **TotalUsage**: Displays stored value + elapsed since SessionStartedAt (live tick while active)
- Avoids stale timing from unclean shutdown; current session always starts fresh

### ActivitySnapshots

- One row represents one device during one minute.
- `Keystrokes` counts key-down events.
- `MouseClicks` counts mouse button-down events.
- `MouseActiveSeconds` stores how many distinct seconds within the minute saw mouse movement.
- The current code persists snapshots, but the main UI primarily exposes live connection/activity state and total usage
  rather than a dedicated activity-history view.

### Device Name Resolution

- `PowershellScripts.GetDeviceName(deviceId)` queries registry (Windows device metadata)
- Falls back to `"Unknown Device"` if lookup fails
- User can rename devices; changes saved immediately to DB

---

## Common Issues & Troubleshooting

| Issue                                                       | Cause                                                | Fix                                                   |
|-------------------------------------------------------------|------------------------------------------------------|-------------------------------------------------------|
| Second launch opens existing window instead of new instance | Mutex check + activation signal                      | Expected behavior                                     |
| Build fails, file locked                                    | KeyPulse.exe running                                 | Stop KeyPulse, then rebuild                           |
| Device shows as "Unknown Device"                            | Windows didn't provide friendly name                 | Rename manually in UI or check Windows Device Manager |
| Duplicate events in debug output                            | Expected behavior; deduped by cache or DB constraint | No action needed                                      |

---

## Key Files to Read First

- `App.xaml.cs` → DI setup, startup/shutdown lifecycle
- `Services/UsbMonitorService.cs` → WMI monitoring, event handling
- `Services/RawInputService.cs` → per-device raw input capture and minute-bucket flushing
- `Services/DataService.cs` → crash recovery, snapshot rebuild, event persistence
- `Models/Device.cs`, `Models/DeviceEvent.cs`, `Models/ActivitySnapshot.cs` → persisted models and runtime state

