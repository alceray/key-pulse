# AGENTS.md: KeyPulse Architecture Guide

## Quick Overview

**KeyPulse** is a .NET 8 WPF desktop app for tracking USB keyboard and mouse connections on Windows and recording
per-device input activity. It uses WMI event watchers for device arrival/removal, Windows Raw Input for per-device
keyboard/mouse activity, and EF Core with SQLite (default) or user-managed PostgreSQL for persistence. The app is single-instance and supports optional
tray/background mode.

**Key Tech Stack**: WPF, EF Core 9, SQLite/PostgreSQL, System.Management (WMI), Windows Raw Input (`WM_INPUT`), Dependency
Injection

---

## Architecture & Data Flow

### Major Components

1. **AppTimerService** (Singleton)
    - Owns all periodic UI-thread timers: 1 second, 30 seconds (dashboard refresh), 1 minute (stats projection), and daily (update check, retention prune)
    - Broadcasts `SecondTick`, `ThirtySecondTick`, `MinuteTick`, and `DailyTick` events to subscriber services and view-models
    - Keeps transient view-models lean by centralizing timer ownership
    - Allows timers to persist across view lifecycle changes (e.g., tab switches)
    - See: `Services/AppTimerService.cs`

2. **UsbMonitorService** (Singleton)
    - Monitors USB device insertion/removal via WMI `__InstanceCreationEvent` and `__InstanceDeletionEvent`
    - Maintains `DeviceList` and `DeviceEventList` as `ObservableCollection<T>` instances for UI binding
    - Screens for HID keyboard/mouse devices only (`kbdhid`, `mouhid` services)
    - Aggregates bursty insert callbacks into one logical `Connected` event per device burst
    - Writes app/device lifecycle events and keeps the in-memory device snapshot in sync
    - Owns the heartbeat timer for crash recovery (separate from UI timers)
    - See: `Services/UsbMonitorService.cs`

3. **RawInputService** (Singleton)
    - Registers hidden `WM_INPUT` listeners using `RIDEV_INPUTSINK`, so activity is captured even in tray/background
      mode
    - Maps raw input handles back to KeyPulse `DeviceId` values
    - Tracks per-device keystrokes, mouse clicks, and mouse-movement-active seconds in one-minute buckets
    - Flushes completed minute buckets to `ActivitySnapshots` every minute and flushes the current partial minute on
      shutdown
    - Raises `ActivityStateChanged` (per-device hold-state) so the UI can highlight currently active devices
    - Raises `InputDeltaIncremented` with per-device `(KeystrokeDelta, MouseClickDelta, MouseMovementDelta)` so live views (e.g. calendar today-overlay) can tick without waiting for the minute flush
    - See: `Services/RawInputService.cs`

4. **DataService** (Singleton)
    - Single source for all database operations
    - Runs the active provider's migration set and enables WAL only for SQLite
    - Crash recovery: detects unclean shutdowns and writes missing `AppEnded`/`ConnectionEnded` events
    - Rebuilds persisted device connection-duration snapshots from the event log on startup
    - Persists and queries minute-level `ActivitySnapshot` rows
    - See: `Services/DataService.cs`

5. **DailyStatsService** (Singleton)
    - Maintains `DailyDeviceStats` table from two write-through sources:
        - **DeviceEvents**: on every closing lifecycle event, recomputes that day's `SessionCount` and `ConnectionSeconds` with a full non-cumulative replay of the day's events
        - **ActivitySnapshots**: minute-delayed projector flushes closed minute buckets to `Keystrokes`, `MouseClicks`, `MouseMovementSeconds`, `ActiveSeconds` (distinct seconds with any input), and `HourlyInputCount` (a `long[24]` of combined input keyed by local clock-hour)
    - `RecomputeDailyDeviceStatsForRange(from, to)` provides an idempotent full rebuild for any date range
    - `RunStartupRebuild()` (called on a background thread from `App.OnStartup`) is the startup entry point:
        - **First run only** (when the `DailyStatsFullBackfillAt` `AppMeta` marker is absent): runs a one-time full historical backfill from the earliest source day through today via `RebuildAllHistory(...)`, chunked month-by-month so memory stays bounded, then writes the marker
        - **Every later run**: runs `ReconcileDriftedDays(today)` — a cheap, non-destructive integrity pass over the current month that heals only `(DeviceId, day)` rows MISSING from `DailyDeviceStats` despite a closing event or activity snapshot existing (the one drift the write-through / projector paths cannot self-heal), logging any discrepancy
    - No recurring blind rebuild: ongoing data is kept current by write-through (connection) + the live projector (activity), which drains all unprojected closed minutes on startup regardless of age
    - All `DailyDeviceStats` mutations (range recompute, live projector, connection write-through) serialize on a single in-process gate so concurrent writers can't collide on the unique `ActivityProjections(DeviceId, Minute)` / `DailyDeviceStats(Day, DeviceId)` indexes
    - See: `Services/DailyStatsService.cs`

6. **DataRetentionService** (Singleton)
    - Applies the user's `ActivityRetentionMonths` setting (default 24; 0 = keep forever) by pruning `ActivitySnapshots` and `ActivityProjections` older than the cutoff day; `DailyDeviceStats` and `DeviceEvents` are always kept, so calendar history and connection totals survive pruning
    - Invariants: never prunes before the `DailyStatsFullBackfillAt` marker exists (a later first-run backfill would recompute old days from pruned sources and zero them); drains `ProjectClosedActivityMinutes()` before deleting; within each batch window deletes snapshots **before** their projection checkpoints (orphan checkpoints are harmless, orphan snapshots would be re-projected and double-counted)
    - Deletes in week-sized chunks (short write transactions under WAL); compacts via `VACUUM` only after large prunes
    - Triggers: chained after `RunStartupRebuild()` on the startup background task, on `DailyTick`, and when the retention setting is tightened
    - See: `Services/DataRetentionService.cs`

7. **ApplicationDbContext** (`DbContext`)
    - Five EF tables: `Devices`, `DeviceEvents`, `ActivitySnapshots`, `DailyDeviceStats`, and `ActivityProjections` (per-minute projection checkpoints), plus the raw `AppMeta` key/value table created lazily outside EF
    - Database stored at `%AppData%\KeyPulse Signal\keypulse-data.db` in Release and `%AppData%\KeyPulse Signal\Test\keypulse-data.db` in Debug/testing builds (folder name = `AppConstants.App.ProductName`, the assembly name)
    - Unique constraint on `DeviceEvents(DeviceId, EventTime, EventType)` prevents duplicate lifecycle events
    - Unique constraint on `ActivitySnapshots(DeviceId, Minute)` prevents duplicate minute buckets
    - See: `Data/ApplicationDbContext.cs`

8. **Database configuration and switching**
    - SQLite remains the default; first-run setup and Settings can select a dedicated PostgreSQL database
    - Settings uses **Save and restart**: save the pending change after confirmation, launch the same executable with preserved arguments, and shut down normally. The replacement waits for the previous process to exit before opening logs, acquiring the mutex, or transferring history; it reopens the main window even in tray mode. See `Services/AppRestartService.cs` and `App.Restart`.
    - Startup database work runs off the UI thread. A themed transfer window reports preparation, copying, verification, and activation, and requests cancellation through Stop and exit. Recovery dialogs stay on the UI thread, and capture begins only after successful preflight. Completion logs include endpoints, current-attempt duration, and whether activation resumed a committed copy. Do not include row counts in completion logs.
    - Non-secret PostgreSQL settings are build-isolated in `settings.json`; passwords use build-qualified Windows Credential Manager entries
    - Direct PostgreSQL-to-PostgreSQL moves keep `PostgreSql` and its credential reference active until activation. `PendingPostgreSql` and its separate credential reference describe the destination. New credentials get unique build-qualified entries, and connection/reference changes publish together through atomic settings writes. Cleanup checks durable references and never deletes the legacy default entry.
    - Direct transfers hold separate nonblocking source and destination advisory locks before target migrations or history reads. Source schema validation is read-only. Destination markers are checked before source access, so committed copies can activate without the source. Recovery can correct either connection's authentication but cannot change confirmed endpoints. See `Services/Database/DatabaseConnectionSettingsService.cs`.
    - `PostgreSqlApplicationDbContext` owns a separate PostgreSQL migration set under `Migrations/PostgreSql`
    - Provider switches run through one preflight/recovery loop before DI/monitoring and copy history in both directions. PostgreSQL replacement requires explicit confirmation. Transfers verify table counts and persisted-content fingerprints in one target transaction and preserve retention metadata.
    - PostgreSQL-to-SQLite exports use a staging file, a verified `.pre-import` backup in `DbBackups`, and a checkpointed file replacement. After successful SQLite activation or initialization, cleanup keeps the three newest automatic backups per database, counting import and migration backups together. SQLite timestamp migration markers prevent repeated UTC conversion.
    - The PostgreSQL advisory lock spans transfer and activation, then transfers to DI ownership when PostgreSQL stays active. Settings are published atomically. Destination-local `DatabaseImportSwitchId` markers make retries complete activation without recopying, including empty histories.
    - Debug and Release must use separate PostgreSQL databases; a PostgreSQL advisory lock prevents concurrent KeyPulse writers
    - See: `Services/Database/DatabaseSwitchService.cs`, `Services/Database/SqliteHistoryFile.cs`, `Data/ConfiguredDbContextFactory.cs`

### Data Persistence Model

- **DeviceEvents** = immutable, append-only log of app/device lifecycle transitions (source of truth for connection
  history)
- **Devices** = mutable, fast-read snapshot used by the UI (`DeviceName`, `DeviceType`, `LastConnectedAt`, stored
  `TotalConnectionSeconds`, `TotalInputCount`, `IsHiddenFromDisplay`)
- **ActivitySnapshots** = immutable minute buckets storing `Keystrokes`, `MouseClicks`, and `MouseMovementSeconds`
- **DailyDeviceStats** = mutable per-day per-device aggregates (`SessionCount`, `ConnectionSeconds`, `Keystrokes`, activity stats etc.) derived from DeviceEvents and ActivitySnapshots; backfilled in full once on first startup, then kept current by write-through + the live projector with a per-startup drift-recovery pass
- **ActivityProjections** = per-(device, minute) checkpoints recording which snapshots were projected into `DailyDeviceStats`; grows in lockstep with `ActivitySnapshots` and is pruned together with it by retention
- Retention prunes only the two per-minute tables; once pruned, `DailyDeviceStats` is the sole surviving record of those days. Pruning has no visible UI effect: the dashboard chart serves hour-aligned bucket tiers (1 Month and longer) from `HourlyInputCount` aggregates via `DashboardHourlyActivityAdapter`, and the raw-minute tiers (1 Day / 1 Week) only ever read windows far newer than the minimum retention cutoff
- Updates flow in two directions:
    - lifecycle changes append to `DeviceEvents` and update the corresponding `Device` snapshot
    - raw input activity accumulates in memory, then flushes to `ActivitySnapshots`

### Startup Sequence (See `App.OnStartup`, `UsbMonitorService`, and `RawInputService`)

1. Register unhandled-exception cleanup hooks.
2. Mutex check (single-instance enforcement). If another instance exists, signal it to restore/focus and exit.
3. Resolve first-run database selection, pending imports, and PostgreSQL connection recovery before capture can start.
   The application starts with explicit shutdown so closing setup/recovery dialogs cannot end startup during an await. Cancellation exits explicitly; the foreground/tray shutdown policy is applied after preflight.
4. Resolve startup mode from build configuration (Debug foreground, Release tray), with launch args able to force tray mode, then build the DI container.
5. Resolve `UsbMonitorService` (which also resolves `DataService`). During construction:
    - database migrations run,
    - SQLite WAL mode is enabled,
    - `DataService.RecoverFromCrash()` backfills missing close events if needed,
    - `DataService.RebuildDeviceSnapshots()` recomputes persisted `TotalConnectionSeconds`, `TotalInputCount`, and `DaysConnected` and clears stale `SessionStartedAt`,
    - historical `Devices` / `DeviceEvents` are loaded,
    - the heartbeat timer starts.
6. `DailyStatsService.RunStartupRebuild()` and `DataRetentionService.RunStartupPrune()` run chained on one background
   task (prune strictly after rebuild, so a first-run backfill never sees pruned sources).
7. Show the main window immediately or initialize the tray icon, depending on background mode.
8. Await `UsbMonitorService.StartAsync()`:
    - `SetCurrentDevicesFromSystem()` writes `AppStarted`, snapshots currently connected HID devices, and emits
      `ConnectionStarted` for each one,
    - then WMI watchers are started for live insert/remove events.
9. Resolve `RawInputService` and call `Start()` to create the hidden message-only window and begin receiving `WM_INPUT`.

### Shutdown / Disposal Ownership

- During normal application exit, DI owns disposal of singleton services via `ServiceProvider.Dispose()`.
- Singleton `Dispose()` methods are idempotent and may still be called from crash/unhandled-exception cleanup paths.
- Duplicate dispose attempts should return immediately and log a `Debug` message rather than performing cleanup twice.

---

## Critical Patterns & Conventions

### Event Types & Lifecycle

Event types are defined in `Models/DeviceEvent.cs` and categorized by `EventTypeExtensions`:

- **Opening**: `ConnectionStarted`, `Connected`
- **Closing**: `ConnectionEnded`, `Disconnected`
- **App-level**: `AppStarted`, `AppEnded`

Device state management is centralized in `UsbMonitorService.AddDeviceEvent()`:

- opening events set `SessionStartedAt`
- closing events commit elapsed time into stored connection duration and clear `SessionStartedAt`
- app-level events are logged without touching per-device state

### Device Identification

- **Format**: `USB\VID_xxxx&PID_xxxx` (parsed from WMI `DeviceID`)
- **Classification**: `UsbDeviceClassifier.GetInterfaceSignal()` probes WMI for `Service`, `ClassGuid`, `PNPClass`
- **Type Resolution**: Multiple HID interfaces expected per device; wait for ≥2 signals before determining type (
  keyboard vs. mouse)
- **Manual Correction**: The Device List context menu lets the user write `Device.DeviceType` directly as Keyboard,
  Mouse, or Other. The DB write must succeed before the shared in-memory device is updated.
- **Raw Input Sanity Check**: `RawInputService` counts consecutive packets that oppose a saved Keyboard/Mouse type.
  After 20 opposing packets it raises one session-only suggestion; `DeviceListViewModel` shows a toast while that tab
  is open. Matching packets reset the count. This never changes the saved type automatically and does not run for
  Unknown/Other devices.

### Property Binding & Threading

- **ObservableObject** base class (in `Helpers/`) wraps `INotifyPropertyChanged`
- Automatically marshals property changes to UI thread via `Application.Current.Dispatcher`
- See `Models/Device.cs` for example: `SessionStartedAt` triggers dependent notifications for `IsConnected` and
  `TotalConnectionSeconds`
- **Important**: `IsConnected` and `IsActive` are different concepts:
    - `IsConnected` means the device currently has an open connection session (`SessionStartedAt.HasValue`)
    - `IsActive` is a transient raw-input hold-state flag used to highlight devices while keys/buttons are currently
      held
    - `IsHiddenFromDisplay` is a persisted user preference that hides a device from **presentation only** (see "Device Display Visibility" below); it never gates capture, event logging, or stat projection
- **Important**: `TotalConnectionSeconds` is computed while connected; its getter returns the stored seconds plus elapsed time since
  `SessionStartedAt`

### Raw Input Activity Semantics

- `RawInputService` creates one in-memory bucket per `(DeviceId, Minute)`.
- **Keyboard**: every key-down increments `Keystrokes`; key-up only updates hold state.
- **Mouse buttons**: every button-down increments `MouseClicks`; button-up only updates hold state.
- **Mouse movement**: movement is recorded as a set of second-of-minute values, then persisted as `MouseMovementSeconds` (
  0–60).
- Completed minutes flush every 60 seconds; dispose/shutdown flushes the current partial minute as well.

### Dashboard Behavior (Connection Duration + Activity)

- **Dashboard refresh**: `DashboardViewModel` subscribes to `AppTimerService.ThirtySecondTick` for periodic refresh
  (Dashboard is transient, so subscriptions are cleaned up when the view is destroyed and re-created when the tab is switched back)
- Dashboard top cards show current connected count and top-3 keyboard/mouse device connection-duration summaries.
- Time-range filter supports `1 Day`, `1 Week`, `1 Month`, `3 Months`, `1 Year`, and `All Time`.
- Activity chart bucket size is **derived from the selected range span**, not user-configurable (`DashboardActivityChartBuilder.ResolveBucketSize`): the finest size on a human-friendly ladder (1/2/5/10/15/30 min, 1/2/3/6/12 h, 1/2 d, 1/2 wk) that keeps the range under `MaxBucketCount` (400) points, so density stays roughly constant (e.g. 1 day → 5 min, 1 week → 30 min, 1 month → 2 h, 3 months → 6 h, 1 year → 1 day). The bucket is the data resolution: **zoom magnifies these points, it never re-aggregates to a finer bucket**. Each active run is plotted as a baseline-anchored polyline (straight segments through one raw per-bucket total at each bucket midpoint, no interpolation or moving-average smoothing) that breaks across inactive runs, so separate-time usage on different devices does not appear to overlap.
- Activity chart zeroes buckets where no app-running interval overlaps (`AppStarted`/`AppEnded` reconstruction).
- Activity chart series use:
    - keyboard = `Keystrokes`
    - mouse = `MouseClicks + MouseMovementSeconds`
- Devices flagged `IsHiddenFromDisplay` are excluded from cards, charts, and the underlying snapshot/event queries (see "Device Display Visibility").
- X-axis label format adapts by selected range:
    - `< 7 days`: `MM-dd HH:mm`
    - `>= 7 days and < 365 days`: `MM-dd`
    - `>= 365 days`: `yyyy-MM`
- Pie charts (`DashboardPieChartBuilder`) show connection-time **share** per device. Slices are colored by connection-time rank from `DashboardDeviceColorPalette` (cool palette = keyboards, warm = mice); devices past `MaxColoredDevicesPerType` (12) or below `OthersShareThreshold` (1%) collapse into a single grey "Others (n)" slice listing the grouped device names. Slices are ordered deterministically so equal values don't flip between refreshes.
- Pie hover tracking surfaces metadata (status, connection time, share, last seen/connected) and pushes it into a bound `DashboardHoverPreview` via OxyPlot tracker events.

### Calendar View Behavior

- **Data source**: `DailyStatsService.GetVisibleDailyDeviceStats()` (plain `DailyDeviceStat` rows, hidden devices excluded); `CalendarSummaryBuilder` (in `ViewModels/Calendar/`) maps rows + in-memory device metadata to tile summaries and detail rows, so the service never references presentation types.
- **Real-time overlay**: `CalendarViewModel` maintains a live in-memory input delta (`_todayLiveDeltaByDevice`) and connection overlay per device; `ApplyRealtimeTodayOverlay()` merges persisted + live state on every second tick and every `RawInputService.InputDeltaIncremented` event. There is **no** periodic DB-refresh timer — the calendar does not subscribe to `ThirtySecondTick`; the persisted baseline is re-read on month load, day selection, and day rollover only.
- **`CalendarDaySummary.IsToday`**: computed property (`Day == DateOnly.FromDateTime(DateTime.Now)`), never stale.
- **`CalendarDaySummary.IsSelected`**: UI-only flag toggled in-place via tile selection; never stored in DB.
- **Calendar DTOs** (`CalendarDaySummary`, `CalendarTileDevice`, `CalendarDeviceDetail`) use the `DeviceTypes` enum, not raw strings, and report connection time in seconds (`ConnectionSeconds`).
- **Hourly activity bars**: `CalendarDeviceDetail.HourlyInputBars` is a 24-slot bar series built by `CalendarHourlyInputBarBuilder` from the day's `HourlyInputCount`; bar heights are normalized to the day's peak hour (`IsPeak` flags it). The same `IReadOnlyList` reference is cached per device across per-second overlay updates so WPF doesn't recreate the bar elements (keeps tooltips alive).
- **Arrow-key navigation**: `MoveSelectionByArrowAsync(±1)` moves the selection across data-bearing tiles, crossing month boundaries when needed; serialized through `_arrowNavigationGate` (a `SemaphoreSlim`) so rapid key-repeats don't interleave.
- Calendar detail panel uses responsive `DockPanel` rows so labels/values stay readable at any panel width.
- Devices flagged `IsHiddenFromDisplay` are excluded from summaries, tiles, and the detail panel; toggling visibility reloads the month (see "Device Display Visibility").

### Duplicate Detection

- WMI fires multiple insert events (~2-3) per physical USB connection
- `_cachedDevices` stores aggregated keyboard/mouse interface signals inside a short time window
- `_pendingCachedDeviceProcessing` ensures only one delayed aggregation callback runs per device burst, preventing a multi-callback race
- Only then is a single `Connected` event recorded
- `DeviceEvents` unique constraint prevents DB duplicates even if code fails

### Device Display Visibility

- `Device.IsHiddenFromDisplay` (persisted on `Devices`, default `false`, migration `AddDeviceDisplayVisibility`) is a **presentation-only** filter. Hidden devices still capture raw input, still append lifecycle events, and are still projected into `DailyDeviceStats` — only their *display* is suppressed.
- **Toggle path**: right-clicking a row in `DeviceListView` opens a context menu ("Hide/Show in Dashboard and Calendar"). `DeviceListViewModel.ToggleDeviceDisplayVisibilityCommand` calls `DataService.SetDeviceHiddenFromDisplay(deviceId, ...)`, and only flips the in-memory `Device.IsHiddenFromDisplay` if the DB write succeeds.
- **Unhide path**: the same context menu, which reads "Unhide Device" on a hidden row. Because the device list keeps hidden rows visible with a badge, the row is always reachable, so hiding and unhiding share one command and one code path.
- **The device list itself does not hide hidden devices** — it shows a "Hidden" badge instead. Only the dashboard and calendar exclude them.
- **Where filtering happens**:
    - `DataService.GetDashboardDevices()` excludes hidden devices at the query.
    - `DashboardViewModel` derives a visible-device id set from that and filters snapshots + events before charting; it refreshes when any `Device.IsHiddenFromDisplay` changes. The activity chart's snapshot source is tiered by range: hour-aligned bucket tiers read `GetVisibleDailyDeviceStats` + `DashboardHourlyActivityAdapter` pseudo-snapshots, finer tiers read raw minute rows.
    - `DailyStatsService` excludes hidden devices from the earliest-day query and `GetVisibleDailyDeviceStats`. A day whose visible rows are empty yields `HasData = false` (via `CalendarSummaryBuilder`).
    - `CalendarViewModel` excludes hidden devices from the realtime overlay, tile summary, and detail panel, and subscribes to per-device `PropertyChanged` (managed via `DeviceList.CollectionChanged`) so toggling visibility reloads the current month.

---

## Workflows & Commands

### TODO Planning

- Create TODO files only when the user explicitly requests one. They are reserved for long tasks that need written steps.
- Every TODO created or substantively updated must include a **code churn estimate**.
- Estimate affected file counts and lines added/deleted as ranges, separating implementation, tests,
  and documentation where useful. State the main assumptions and update the estimate if scope changes.
- Label estimates clearly; do not present them as measured diffs. For tasks without code changes,
  state that code churn is zero and describe the expected documentation or asset changes instead.

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

- **AppConstants.App** structure consolidates application-level settings:
  - `ProductName` = "KeyPulse Signal" (from assembly)
  - `DefaultName` = "KeyPulse Signal" or "KeyPulse Signal (Test)" depending on Debug/Release build
  - `TrayArgument` = `--tray`
  - `ActivationEventSuffix` = `.ACTIVATE`
  - `RunKeyPath` = Windows registry path for startup entries
  - `TrayIconRelativePath` = relative path to the taskbar icon (`Assets\keypulse-signal-icon.ico`)
  - `StartupWarningBalloonTimeoutMs` = 5000 (milliseconds for startup warning balloon)
- **Build mode default**: Debug launches windowed; Release launches to tray/background
- **`LaunchOnLogin` default**: `false` in Debug, `true` in Release (controlled via `#if DEBUG` in `AppUserSettings`)
- **Launch args**: `--tray` forces tray/background startup for that process
- **Application icon**: Set via `<ApplicationIcon>` in `.csproj` for both window and taskbar display; also used for tray icon
- Tray icon created if background mode enabled; main window created on-demand or at startup

### Build & Run

- Target: `.net8.0-windows`, WPF enabled
- Debug builds auto-generate WPF XAML behind the scenes
- **Build may fail** if `KeyPulse Signal.exe` is locked — stop running instance first
- **Assets**: `Assets\keypulse-signal-icon.ico` (the `<ApplicationIcon>`) copied to build output

---

## File Organization & Responsibilities

| Folder        | Purpose                                                                                                          |
|---------------|------------------------------------------------------------------------------------------------------------------|
| `Helpers/`    | `ObservableObject`, `RelayCommand`, `UsbDeviceClassifier`, `TimeFormatter`, `PowerShellScripts`, `HeartbeatFile` |
| `Services/`   | `UsbMonitorService`, `RawInputService`, `DataService`, `DailyStatsService` — monitoring, activity capture, persistence, daily stats |
| `Models/`     | `Device`, `DeviceEvent`, `ActivitySnapshot`, `DailyDeviceStat` + enums/extensions                               |
| `Data/`       | `ApplicationDbContext`, database initialization                                                                  |
| `Configuration/` | `AppConstants` plus `AppColorPalette` — the single source of light/dark UI colors. Brushes stay frozen; XAML uses `{DynamicResource PrimaryTextBrush}` (and the other named keys) so existing controls update immediately. Shared control templates live in `Views/Styles/ThemeControls.xaml`, and per-view reusable styles in `Views/Styles/AppStyles.xaml`; both are merged globally in `App.xaml`. The OxyPlot device palette (`DashboardDeviceColorPalette`) is intentionally separate. |
| `ViewModels/` | MVVM viewmodels for each UI view; `StatusMessageViewModelBase` provides shared status toast behavior. `Dashboard/` and `Calendar/` subfolders hold the chart/pie/color builders, DTOs, and per-view helpers extracted from the larger view-models |
| `Views/`      | XAML + code-behind for UI; `StatusMessagePanel` is a reusable status toast control; `SharedConverters.cs` holds shared `IValueConverter`s (e.g. `InverseBoolToVisibilityConverter`, `DurationSecondsConverter`); `Styles/AppStyles.xaml` holds the app-wide control styles |
| `Migrations/` | EF Core snapshot migrations (read-only; auto-generated)                                                          |
| `docs/`       | release docs, production-readiness plan, and other project documentation                                         |

---

## Crash Recovery & Consistency

**Problem**: If app is force-killed (IDE stop, crash), devices may appear "stuck" as active in the DB.

**Solution** (`DataService.RecoverFromCrash`):

1. On startup, check if last app-lifecycle event was `AppStarted` (unmatched)
2. If so, retroactively add `AppEnded` and `ConnectionEnded` for orphaned devices
3. Event log stays consistent for future connection-duration calculations

**Snapshot Rebuild** (`DataService.RebuildDeviceSnapshots`):

- Recomputes persisted `TotalConnectionSeconds` from the event log (DeviceEvents are never pruned, so the event-pairing replay stays exact)
- Recomputes persisted `TotalInputCount` from `DailyDeviceStats` sums plus any snapshots without a projection checkpoint — exact in every DB state (pre-backfill, steady-state, and after retention pruned old snapshots)
- Recomputes persisted `DaysConnected` as the count of `DailyDeviceStats` rows with connection time (one row per local day, never pruned)
- Clears runtime-only `SessionStartedAt` so devices do not appear connected after an unclean shutdown
- Called at startup after recovery

---

## Important Implementation Details

### Duplicate Event Prevention

- `DeviceInsertedEvent` accumulates keyboard/mouse signals until ≥2 are seen within a short timeframe
- Only emits one `Connected` event per physical device insertion
- DB unique constraint `(DeviceId, EventTime, EventType)` is secondary safeguard

### Current Session Connection Duration ("Session" vs. "Total")

- **SessionStartedAt**: Set when device becomes active (opening event), cleared when inactive
- **IsConnected**: Computed from `SessionStartedAt.HasValue`
- **IsActive**: Separate transient hold-state driven by `RawInputService.ActivityStateChanged`
- **TotalConnectionSeconds**: getter returns stored seconds + elapsed since SessionStartedAt (live tick while active)
- Avoids stale timing from unclean shutdown; current session always starts fresh

### ActivitySnapshots

- One row represents one device during one minute.
- `Keystrokes` counts key-down events.
- `MouseClicks` counts mouse button-down events.
- `MouseMovementSeconds` stores how many distinct seconds within the minute saw mouse movement.
- The current code persists snapshots, but the main UI primarily exposes live connection/activity state and total connection duration
  rather than a dedicated activity-history view.

### Device Name Resolution

- `DeviceNameLookup.GetDeviceName(deviceId)` first attempts native SetupAPI/cfgmgr32 lookup
- Falls back to `PowershellScripts.GetDeviceName(deviceId)` only if native lookup fails
- Falls back to `"Unknown Device"` if lookup fails
- Native lookup and PowerShell fallback paths log at `Debug`; lookup failures that throw log at `Warning`
- User can rename devices; changes saved immediately to DB

### Helper Logging Conventions

- Pure helpers such as `RelayCommand`, `AsyncRelayCommand`, and `ObservableObject` should stay log-free. `TimeFormatter` is log-free except `ToRelativeTime`, which logs an error when given a future timestamp.
- Log messages should state the condition in domain terms and must not expose internal code details (method or class names). For example, `ToRelativeTime`'s future-timestamp log reads `"Future timestamp received"`, not the method name.
- Helpers that touch the file system, PowerShell, SetupAPI/cfgmgr32, or crash-recovery inputs should log meaningful failures.
- `HeartbeatFile.Read()` should log `Debug` when no heartbeat file exists and `Warning` if the file exists but contains an invalid timestamp.
- `UsbDeviceClassifier.ResolveDeviceType()` logs `Warning` when the observed signal pattern does not match a known keyboard/mouse shape.

### TimeFormatter Helpers

- `ToLocalTime(DateTime)` — converts any `DateTime` to local time, treating `Unspecified` as UTC (matches SQLite reads).
- `ToLocalDay(DateTime)` — converts a UTC/unspecified timestamp to the local `DateOnly` day. Use this everywhere instead of `DateOnly.FromDateTime(ToLocalTime(...))`.
- `LocalDayToUtc(DateOnly)` — converts a local day to its UTC start boundary for inclusive range queries.
- `NormalizeUtcMinute(DateTime)` — converts to UTC then truncates to minute boundary; used by `DailyStatsService` projector.
- `TruncateToMinute(DateTime)` — truncates to minute while preserving `DateTimeKind`; used by `RawInputService` for local-time minute buckets. **Not interchangeable with `NormalizeUtcMinute`.**
- Timestamp truncation uses tick arithmetic to preserve the distinction between the two occurrences of a local clock time when daylight saving time ends. Do not reconstruct local timestamps from calendar components in the truncation helpers.

### Status Message Pattern

- `StatusMessageViewModelBase` (`ViewModels/`) provides `StatusMessage`, `StatusVisibility`, and auto-clear timer behavior.
- ViewModels that need transient toast messages should inherit `StatusMessageViewModelBase` instead of duplicating these fields.
- `StatusMessagePanel` (`Views/`) is the reusable XAML control with `StatusMessage` and `StatusVisibility` dependency properties.
- Positioning (alignment, margin, grid row) is controlled by the host view — the panel itself is layout-agnostic.

### Application Theme

- `AppUserSettings.DarkMode` defaults to `false` and is saved by the General section in Settings.
- `ThemeService` applies the saved palette before database setup/recovery windows open, then subscribes to settings changes through DI. All resource updates and `ThemeChanged` notifications run on the UI thread.
- Use dynamic brush resources in XAML, including trigger setters. Log `Run` elements use `SetResourceReference` so a switch preserves the existing document, search match, and scroll position.
- `DashboardViewModel` rebuilds plots from cached inputs on a theme change (no database query). `DashboardPlotTheme.Apply` runs after fresh series are installed on the UI thread, preserving activity axes and zoom; it expects original device-series colors, not previously themed colors.
- Windows 11 title bars follow the theme through `WindowTheme`. Native Windows message boxes and tray menus retain their system appearance.

### Dispatcher Safety in Services

Services that receive background callbacks (WMI events, `RawInputService` message pump) and update UI-bound collections must guard Dispatcher calls.
Use `ShutdownDispose.IsDispatcherUsable(dispatcher)` before any `Invoke`/`BeginInvoke`. Two shutdown scenarios require this:
1. **Normal shutdown**: `_disposed = true` is set at Dispose start; subsequent callbacks skip UI updates.
2. **Windows session end**: disposal is skipped (`_disposed` stays false) but the Dispatcher may be dead; `HasShutdownStarted`/`HasShutdownFinished` checks prevent deadlocks.

Pure services (`DataService`, `DailyStatsService`, helpers) do **not** need dispatcher guards — they have no UI bindings.

### Troubleshooting View Auto-scroll Behavior

- Log viewer automatically scrolls to the bottom when:
  - The tab becomes visible (`IsVisibleChanged` event fires true)
  - New log content is loaded (`LogContent` property changes)
  - Logs are refreshed via `RefreshLogs()` command
- Scrolling uses `DispatcherPriority.Background` to allow layout to settle before scroll
- Search and entry parsing preserve scroll position only when actively searching; log load and visibility changes always trigger scroll-to-bottom

### Documentation Entry Points

- `README.md` = project overview, quick-start, and release doc links
- `docs/RELEASE_PROCESS.md` = versioning and packaging workflow
- `docs/PRODUCTION_READINESS_PLAN.md` = tracked production readiness plan

---

## Common Issues & Troubleshooting

| Issue                                                       | Cause                                                | Fix                                                   |
|-------------------------------------------------------------|------------------------------------------------------|-------------------------------------------------------|
| Second launch opens existing window instead of new instance | Mutex check + activation signal                      | Expected behavior                                     |
| Build fails, file locked                                    | `KeyPulse Signal.exe` running                        | Stop KeyPulse, then rebuild                           |
| Device shows as "Unknown Device"                            | Windows didn't provide friendly name                 | Rename manually in UI or check Windows Device Manager |
| Duplicate events in debug output                            | Expected behavior; deduped by cache or DB constraint | No action needed                                      |

---

## Key Files to Read First

- `App.xaml.cs` → DI setup, startup/shutdown lifecycle
- `Services/UsbMonitorService.cs` → WMI monitoring, event handling
- `Services/RawInputService.cs` → per-device raw input capture and minute-bucket flushing
- `Services/DataService.cs` → crash recovery, snapshot rebuild, event persistence
- `Services/DailyStatsService.cs` → daily stats write-through, activity projector, startup rebuild
- `ViewModels/CalendarViewModel.cs` → calendar real-time overlay, persisted baseline refresh
- `ViewModels/StatusMessageViewModelBase.cs` → shared status toast VM base
- `Views/StatusMessagePanel.xaml` → reusable status toast control
- `Models/Device.cs`, `Models/DeviceEvent.cs`, `Models/ActivitySnapshot.cs`, `Models/DailyDeviceStat.cs` → persisted models and runtime state
