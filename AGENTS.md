# AGENTS.md: KeyPulse Architecture Guide

## Quick Overview
**KeyPulse** is a .NET 8 WPF desktop app tracking USB keyboard/mouse usage on Windows. It uses WMI event watchers for live device detection and SQLite+EF Core for persistence. Single-instance with optional tray mode.

**Key Tech Stack**: WPF, EF Core 9, SQLite, System.Management (WMI), Dependency Injection

---

## Architecture & Data Flow

### Major Components
1. **UsbMonitorService** (Singleton)
   - Monitors USB device insertion/removal via WMI `__InstanceCreationEvent` and `__InstanceDeletionEvent`
   - Maintains `DeviceList` and `DeviceEventList` as ObservableCollections for UI binding
   - Screens for HID keyboard/mouse devices only (`kbdhid`, `mouhid` services)
   - See: `Services/UsbMonitorService.cs`

2. **DataService** (Scoped)
   - Single source for all database operations
   - Crash recovery: detects unclean shutdowns and writes missing `AppEnded`/`ConnectionEnded` events
   - Rebuilds device snapshots from event log on startup
   - See: `Services/DataService.cs`

3. **ApplicationDbContext** (DbContext)
   - Two tables: `Devices` (DeviceInfo snapshots) and `DeviceEvents` (immutable event log)
   - Database stored at `%AppData%\KeyPulse\devices.db`
   - Unique constraint on `(DeviceId, Timestamp, EventType)` prevents duplicate events
   - See: `Data/ApplicationDbContext.cs`

### Data Persistence Model
- **DeviceEvents** = immutable, append-only log of all lifecycle events (source of truth)
- **DeviceInfo** = mutable, cached snapshot rebuilt from events at startup
- Updates persist bidirectionally: events saved after device state changes, device snapshots updated from events

### Startup Sequence (See `App.OnStartup` + `UsbMonitorService.ctor`)
1. Mutex check (single-instance enforcement)
2. DI container setup
3. `DataService.RecoverFromCrash()` — writes missing AppEnded if previous session crashed
4. `DataService.RebuildDeviceSnapshots()` — recompute TotalUsage from event log
5. Load historical devices/events from DB
6. `UsbMonitorService.SetCurrentDevicesFromSystem()` — snapshot currently-connected devices, emit `ConnectionStarted` events
7. Start WMI watchers for live device changes

---

## Critical Patterns & Conventions

### Event Types & Lifecycle
Four event categories defined in `Models/DeviceEvent.cs` and `EventTypeExtensions`:
- **Opening** (device becomes active): `ConnectionStarted`, `Connected`, `Resumed`
- **Closing** (device becomes inactive): `ConnectionEnded`, `Disconnected`, `Suspended`
- **App-level** (no specific device): `AppStarted`, `AppEnded`

Device state machine: Use `IsOpeningEvent()`/`IsClosingEvent()` extensions for state logic. See `UsbMonitorService.AddDeviceEvent()`.

### Device Identification
- **Format**: `USB\VID_xxxx&PID_xxxx` (parsed from WMI `DeviceID`)
- **Classification**: `UsbDeviceClassifier.GetInterfaceSignal()` probes WMI for `Service`, `ClassGuid`, `PNPClass`
- **Type Resolution**: Multiple HID interfaces expected per device; wait for ≥2 signals before determining type (keyboard vs. mouse)

### Property Binding & Threading
- **ObservableObject** base class (in `Helpers/`) wraps `INotifyPropertyChanged`
- Automatically marshals property changes to UI thread via `Application.Current.Dispatcher`
- See `DeviceInfo` for example: `SessionStartedAt` setter triggers notifications for dependent properties (`IsActive`, `TotalUsage`)
- **Important**: TotalUsage is **computed** while active; displays stored value + elapsed since `SessionStartedAt`

### Duplicate Detection
- WMI fires multiple insert events (~2-3) per physical USB connection
- `_recentlyInsertedDevices` cache (with timeout cleared on removal) de-duplicates by collecting signals until ≥2 confirm device type
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
- **App.config**: `RunInBackground` setting controls startup mode
- **Env Var**: `KEYPULSE_RUN_IN_BACKGROUND` overrides config (checked in `App.OnStartup`)
- Tray icon created if background mode enabled; main window created on-demand or at startup

### Build & Run
- Target: `.net8.0-windows`, WPF enabled
- Debug builds auto-generate WPF XAML behind the scenes
- **Build may fail** if KeyPulse.exe is locked — stop running instance first
- **Assets**: `Assets/keyboard_mouse_icon.ico` copied to build output

---

## File Organization & Responsibilities

| Folder | Purpose |
|--------|---------|
| `Helpers/` | `ObservableObject`, `RelayCommand`, `UsbDeviceClassifier`, `TimeFormatter`, `PowerShellScripts` |
| `Services/` | `UsbMonitorService`, `DataService` — core logic |
| `Models/` | `DeviceInfo`, `DeviceEvent` + enums/extensions |
| `Data/` | `ApplicationDbContext`, database initialization |
| `ViewModels/` | MVVM viewmodels for each UI view (e.g., `DeviceListViewModel`) |
| `Views/` | XAML + code-behind for UI (e.g., `DeviceListView.xaml`) |
| `Migrations/` | EF Core snapshot migrations (read-only; auto-generated) |

---

## Crash Recovery & Consistency

**Problem**: If app is force-killed (IDE stop, crash), devices may appear "stuck" as active in the DB.

**Solution** (`DataService.RecoverFromCrash`):
1. On startup, check if last app-lifecycle event was `AppStarted` (unmatched)
2. If so, retroactively add `AppEnded` and `ConnectionEnded` for orphaned devices
3. Event log stays consistent for future usage calculations

**Snapshot Rebuild** (`DataService.RebuildDeviceSnapshots`):
- Recomputes `TotalUsage` and `LastConnectedAt` from event log
- Clears runtime-only `SessionStartedAt` so Devices don't appear artificially active
- Called at startup after recovery

---

## Important Implementation Details

### Duplicate Event Prevention
- `DeviceInsertedEvent` accumulates keyboard/mouse signals until ≥2 are seen within a short timeframe
- Only emits one `Connected` event per physical device insertion
- DB unique constraint `(DeviceId, Timestamp, EventType)` is secondary safeguard

### CurrentSessionUsage ("Session" vs. "Total")
- **SessionStartedAt**: Set when device becomes active (opening event), cleared when inactive
- **IsActive**: Computed from `SessionStartedAt.HasValue`
- **TotalUsage**: Displays stored value + elapsed since SessionStartedAt (live tick while active)
- Avoids stale timing from unclean shutdown; current session always starts fresh

### Device Name Resolution
- `PowershellScripts.GetDeviceName(deviceId)` queries registry (Windows device metadata)
- Falls back to `"Unknown Device"` if lookup fails
- User can rename devices; changes saved immediately to DB

---

## Common Issues & Troubleshooting

| Issue | Cause | Fix |
|-------|-------|-----|
| "Already running" on launch | Mutex check triggered | Kill existing KeyPulse process |
| Build fails, file locked | KeyPulse.exe running | Stop KeyPulse, then rebuild |
| Device shows as "Unknown Device" | Windows didn't provide friendly name | Rename manually in UI or check Windows Device Manager |
| Duplicate events in debug output | Expected behavior; deduped by cache or DB constraint | No action needed |

---

## Key Files to Read First
- `App.xaml.cs` → DI setup, startup/shutdown lifecycle
- `Services/UsbMonitorService.cs` → WMI monitoring, event handling
- `Services/DataService.cs` → crash recovery, snapshot rebuild, event persistence
- `Models/DeviceInfo.cs` + `Models/DeviceEvent.cs` → data model, event lifecycle

