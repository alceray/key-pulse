# KeyPulse Production Readiness Plan

## Phase 1 - Stabilize startup and configuration

### Goal

Make startup behavior deterministic and aligned with intended product behavior.

### Work items

#### 1.1 Standardize startup mode strategy

- **Files:** `App.xaml.cs`
- **Status:** Completed.
- **Implemented:** Startup mode resolution centralized in `ResolveRunInBackground()` with clear precedence:
  1. launch arguments `--tray` or `--startup` force tray mode for that process
  2. otherwise build default applies:
    - `Debug` => foreground window
    - `Release` => tray/background
- **Acceptance criteria:**
  - `Debug` launches foreground by default ✅
  - `Release` launches tray by default ✅
  - `--tray` / `--startup` consistently force tray in both build configs ✅

#### 1.2 Define "auto-start launch behavior"

- **Files:** `App.xaml.cs`
- **Status:** Completed.
- **Implemented:** Tray-first startup behavior integrated:
  - When launched from startup/login:
    - does not show the main window
    - initializes tray icon immediately
    - begins startup work in background
    - exposes UI only via tray click/menu
  - Startup entry arguments (`--startup` or `--tray`) force tray-first behavior
- **Acceptance criteria:**
  - app starts silently in tray after login ✅
  - no disruptive main window popup at boot ✅

#### 1.3 Foreground the existing instance

- **Files:** `App.xaml.cs`
- **Status:** Completed.
- **Implemented:** Single-instance mutex enforcement with instance activation signal:
  - On second launch:
    - checks for existing instance via named mutex
    - signals the existing instance to show/activate the main window
    - exits the second process
  - Uses mutex + `WM_SHOWWINDOW` message for IPC
- **Acceptance criteria:**
  - double launching the app restores the first instance instead of just showing a message ✅

---

## Phase 2 - Add persistent logging

### Goal

Use structured, persistent file logging for startup/runtime/shutdown diagnostics.

### Work items

#### 2.1 Wire up structured logging

- **Files:** `App.xaml.cs`, `KeyPulse.csproj`
- **Status:** Completed.
- **Implemented:** Serilog startup initialization with file sink under `%AppData%\KeyPulse\Logs\`
- **Recommended log files:**
  - rolling daily logs
  - retained for a limited number of days/files
- **Added package:**
  - `Serilog.Sinks.File`
- **Current log level policy:**
  - `Debug` build: `Debug+`
  - `Release` build: `Information+`
- **Acceptance criteria:**
  - app writes logs on startup, shutdown, and failures ✅
  - logs persist after app restarts ✅
  - `Release` no longer emits `Debug` events by default ✅

#### 2.2 Replace `Debug.WriteLine`

- **Files:**
  - `App.xaml.cs`
  - `Services/DataService.cs`
  - `Services/UsbMonitorService.cs`
  - `Services/RawInputService.cs`
  - `Helpers/HeartbeatFile.cs`
  - `Helpers/PowerShellScripts.cs`
- **Status:** Completed.
- **Implemented:** Replaced `Debug.WriteLine(...)` with structured logging calls:
  - information
  - warning
  - error
- **Minimum events to log:**
  - startup/shutdown
  - tray init
  - WMI watcher start/stop
  - raw input registration success/failure
  - database migration/rebuild/recovery
  - duplicate event suppression
  - exceptions in service flows
- **Acceptance criteria:**
  - no critical operational diagnostics depend solely on debugger output ✅
  - no `Debug.WriteLine` usage remains in target Phase 2 files ✅

---

## Phase 3 - Harden shutdown, crash recovery, and long-running behavior

### Goal

Make the app reliable as an always-running background utility.

### Work items

#### 3.1 Audit shutdown paths

- **Files:** `App.xaml.cs`, `Services/UsbMonitorService.cs`, `Services/RawInputService.cs`
- **Implement:** Verify all shutdown paths:
  - manual exit from tray
  - Windows logoff
  - Windows shutdown
  - unhandled exception
  - hidden-window/tray-only mode
- **Ensure:**
  - `RawInputService.Dispose()` flushes pending activity safely
  - `UsbMonitorService.Dispose()` stops watchers cleanly
  - tray icon is disposed
  - heartbeat file is cleared if appropriate
- **Acceptance criteria:**
  - normal exit leaves DB/event state consistent
  - no orphan tray icons after app exit

#### 3.2 Improve crash recovery observability

- **Files:** `Services/DataService.cs`
- **Implement:** Keep current recovery logic, but add:
  - detailed logs of what recovery did
  - count of backfilled events
  - device IDs affected
  - recovery duration
- **Acceptance criteria:**
  - crash recovery behavior is diagnosable from logs

#### 3.3 Add retry/degraded-mode startup

- **Files:** `App.xaml.cs`, `Services/UsbMonitorService.cs`, `Services/RawInputService.cs`
- **Implement:** If one subsystem fails:
  - app should still come up in tray if possible
  - show a user-facing warning if tracking is partially unavailable
  - keep app responsive rather than failing the entire startup
- **Examples:**
  - WMI watcher init fails -> log, retry, continue with known devices
  - PowerShell naming fails -> use `Unknown Device`
  - Raw Input registration fails -> degrade activity tracking but keep lifecycle tracking
- **Acceptance criteria:**
  - single subsystem failure does not necessarily kill the entire app session

---

## Phase 4 - Implement "Start with Windows"

### Goal

Support reliable auto-start at user logon.

### Work items

#### 4.1 Add an autostart abstraction

- **Files:** new file (e.g. `Services/StartupRegistrationService.cs`), optional settings model/viewmodel files
- **Implement:** Add methods such as:
  - `bool IsEnabled()`
  - `void Enable()`
  - `void Disable()`
- **Preferred implementation:** per-user registry key `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
- **Note:** Command should point to installed exe and include optional background/startup argument if needed.
- **Acceptance criteria:**
  - app can enable/disable start-with-Windows for current user

#### 4.2 Add a setting for "Start with Windows"

- **Files:** settings model/service (new), relevant view/viewmodel if exposed in UI
- **Implement:** Persist a user-facing preference for:
  - launch at login
  - optionally "start minimized to tray"
- **Acceptance criteria:**
  - user can toggle startup behavior without editing config manually

#### 4.3 Support explicit startup arguments

- **Files:** `App.xaml.cs`
- **Implement:** Support launch arguments:
  - `--tray`
  - `--startup`
- **Use:** force tray launch when invoked from the Run key.
- **Acceptance criteria:**
  - auto-start can reliably start hidden/tray mode regardless of default app launch behavior

---

## Phase 5 - Fix release build and packaging basics

### Goal

Make release output suitable for actual deployment.

### Work items

#### 5.1 Correct release configuration

- **Files:** `KeyPulse.csproj`
- **Current issue:** Release currently defines `DEBUG`.
- **Implement:**
  - remove `DEBUG` from Release constants
  - enable optimization
  - configure deterministic release behavior
- **Acceptance criteria:**
  - Release build is truly production-oriented

#### 5.2 Decide install/publish strategy

- **Files:** likely new installer/project files (not yet present)
- **Recommended options:**
  - **Option A: Inno Setup / WiX** (best fit for current app style)
    - install app under user or program files
    - optionally create startup entry
    - create Start Menu shortcut
    - uninstall cleanly
    - preserve `%AppData%\KeyPulse\devices.db`
  - **Option B: MSIX**
    - possible, but validate tray/raw-input/WMI compatibility and startup behavior before committing
- **Recommendation:** Start with Inno Setup or WiX for fastest path.
- **Acceptance criteria:**
  - clean install
  - clean uninstall
  - upgrade works without data loss

#### 5.3 Preserve user data across upgrades

- **Files:** installer config, possibly migration/recovery docs
- **Implement:** Ensure installer/uninstaller does not remove:
  - `%AppData%\KeyPulse\devices.db`
  - logs unless user chooses full cleanup
- **Acceptance criteria:**
  - upgrading app keeps history and settings intact

---

## Phase 6 - Settings, UX, and supportability

### Goal

Make the app manageable for real users.

### Work items

#### 6.1 Add a settings surface

- **Files:** likely new settings view/viewmodel; maybe `MainWindow.xaml` / settings page
- **Settings to add:**
  - Start with Windows
  - Run in background / start minimized to tray
  - Open window on launch
  - Optional log level
  - Optional "reset diagnostics" / "open logs folder"
- **Acceptance criteria:**
  - key operational settings are discoverable in UI

#### 6.2 Improve tray UX

- **Files:** `App.xaml.cs`
- **Add tray menu items:**
  - Open
  - Start with Windows (toggle)
  - Run in background (toggle)
  - Open logs folder
  - Exit
- **Acceptance criteria:**
  - most common actions can be managed without opening main UI

#### 6.3 Add user-visible startup failure messaging

- **Files:** `App.xaml.cs`
- **Implement:** If startup is partially broken:
  - show a single tray balloon / dialog / notification
  - direct user to logs
- **Acceptance criteria:**
  - failures are visible without attaching a debugger

---

## Phase 7 - Reduce operational risk

### Goal

Lower support burden and avoid preventable failures.

### Work items

#### 7.1 Replace or reduce PowerShell dependency

- **Files:** `Helpers/PowerShellScripts.cs`, callers in `UsbMonitorService` / helpers
- **Why:** PowerShell in background apps can be slower, policy-restricted, and less reliable in enterprise environments.
- **Implement:** Prefer direct Windows-native lookup if feasible (registry/SetupAPI/WMI without spawning PowerShell).
- **Acceptance criteria:**
  - device-name lookup no longer depends on launching PowerShell, or at least has strong fallback behavior

#### 7.2 Add DB backup / migration safety plan

- **Files:** `Services/DataService.cs`, installer/update docs, maybe helper utilities
- **Implement:** Before applying risky future migrations:
  - optionally create timestamped DB backup
  - log migration versions
  - handle migration exceptions clearly
- **Acceptance criteria:**
  - corrupted or failed upgrade is recoverable

#### 7.3 Add code signing

- **Files:** CI/release pipeline, installer config
- **Implement:** Sign:
  - executable
  - installer
- **Acceptance criteria:**
  - fewer SmartScreen/trust issues
  - more production-ready distribution

---

## Phase 8 - Updates and release operations

### Goal

Support long-term maintenance.

### Work items

#### 8.1 Add versioned release process

- **Files:** release docs, pipeline config if present later
- **Implement:** Define:
  - semantic or date-based versioning
  - release notes
  - upgrade testing checklist
- **Acceptance criteria:**
  - reproducible release process

#### 8.2 Add auto-update path (optional but recommended)

- **Options:**
  - installer-driven updates
  - winget distribution
  - custom update check
  - MSIX/App Installer if you go that route
- **Acceptance criteria:**
  - users can upgrade without manual uninstall/reinstall

---

## Proposed file-level work map

### Immediate changes

- `App.xaml.cs`
  - startup mode resolution
  - startup args
  - single-instance activation
  - log bootstrap
  - improved shutdown handling
- `KeyPulse.csproj`
  - fix Release config
  - add logging sink packages if needed
- `Services/DataService.cs`
  - structured logging
  - migration/recovery logging improvements
- `Services/UsbMonitorService.cs`
  - structured logging
  - retry/degraded-mode hooks
- `Services/RawInputService.cs`
  - structured logging
  - better startup/registration failure handling
- `Helpers/HeartbeatFile.cs`
  - structured logging
- `Helpers/PowerShellScripts.cs`
  - structured logging
  - stronger fallback/error handling

### New likely files

- `Services/StartupRegistrationService.cs`
- `Services/AppSettingsService.cs` (or similar)
- settings model/viewmodel/view
- installer scripts/config

---

## Recommended delivery order (milestones)

### Milestone 1 - Operational baseline

- startup config fix
- release config fix
- Serilog integration
- log replacement
- tray-first boot polish

### Milestone 2 - Auto-start

- startup registration service
- `--startup` / `--tray` argument support
- UI toggle for Start with Windows

### Milestone 3 - Packaging

- installer
- upgrade path
- preserved DB/log data
- signed builds if possible

### Milestone 4 - Hardening

- retry/degraded startup
- better crash diagnostics
- PowerShell dependency cleanup
- better support tooling

---

## Definition of done

KeyPulse is production-ready when all of these are true:

- installs via a real installer
- can enable/disable "Start with Windows"
- starts silently to tray at login
- logs operational issues to disk
- survives crashes and reboots with consistent DB state
- upgrades without losing user data
- uses a real release build configuration
- exposes key behavior through settings/UI, not just config/env vars
