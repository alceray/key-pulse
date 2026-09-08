# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

KeyPulse Signal is a .NET 8 WPF (Windows-only) desktop app that tracks USB keyboard/mouse connections and per-device input activity. It uses WMI watchers for device arrival/removal, Windows Raw Input (`WM_INPUT`) for activity capture, and SQLite + EF Core 9 for persistence. MVVM with `Microsoft.Extensions.DependencyInjection`; Serilog for logging; OxyPlot for charts.

**`AGENTS.md` is the authoritative architecture reference** — read it for data flow, lifecycle semantics, crash recovery, and per-subsystem detail. This file covers commands and the orientation needed to find your way around.

## Commands

```powershell
dotnet build -c Debug          # build (Debug = windowed; Release = tray/background)
dotnet run -c Debug            # run locally
dotnet csharpier .             # format (printWidth 120, see .csharpierrc.json)
dotnet test "KeyPulse Signal.sln"   # run all tests (NOT bare `dotnet test` — see note below)

# EF Core migrations (run from repo root). A schema change needs BOTH provider sets.
dotnet ef migrations add <Name>                                                             # SQLite
dotnet ef migrations add <Name> -c PostgreSqlApplicationDbContext -o Migrations/PostgreSql  # PostgreSQL
dotnet ef migrations remove
dotnet ef database update

.\scripts\Build-Release.ps1 [-Version "1.2.0"]   # publish + Inno Setup installer (needs iscc.exe on PATH)
```

- **Tests** live in `KeyPulse.Tests/` (xUnit + Shouldly). Run them via the solution: `dotnet test "KeyPulse Signal.sln"`. Bare `dotnet test` fails (`MSB1011`) because `KeyPulse.csproj` sits beside the `.sln` at the repo root — always name the `.sln` (or `KeyPulse.Tests`). Beyond unit-testing pure helpers, DB-backed tests use the `SqliteTestDatabase` fixture (throwaway file SQLite, real EF model). Behavior that needs the live WMI/Raw Input/WPF stack is still verified manually by running the app.
- Migrations run automatically on startup via `DataService`; you usually only `add` a migration, not `database update` by hand.
- **Two migration sets.** SQLite lives in `Migrations/` and PostgreSQL in `Migrations/PostgreSql/`, owned by `ApplicationDbContext` and `PostgreSqlApplicationDbContext` respectively. The bare `add` command targets SQLite because it is the default provider, so the PostgreSQL one always needs `-c` and `-o`. Any model change requires both, and skipping the PostgreSQL half fails at startup on that provider rather than at build time. `PostgreSqlDesignTimeDbContextFactory` is inside `#if DEBUG`, so run the command in a Debug configuration. Its connection string is a placeholder, so `add` needs no reachable server.
- **Build fails if a KeyPulse instance is running** (locked `KeyPulse Signal.exe`/`.dll`) — stop it first. This also blocks `dotnet test`, since it rebuilds the app project.
- `--tray` launch arg forces tray/background mode.

## Architecture orientation

All services are **DI singletons** registered in `App.xaml.cs` (`OnStartup`). The key ones:

- **`AppTimerService`** — owns the shared UI-thread timers (1s / 30s / 1min / daily) and broadcasts tick events, so transient view-models don't each spin up timers.
- **`UsbMonitorService`** — WMI device monitoring, insert-burst deduplication, connection lifecycle; owns the `ObservableCollection`s the UI binds to.
- **`RawInputService`** — hidden `WM_INPUT` window capturing per-device keystrokes/clicks/movement into in-memory minute buckets, flushed to the DB every minute.
- **`DataService`** — single source for all DB ops; runs migrations, enables WAL, performs crash recovery and snapshot rebuild on startup.
- **`DailyStatsService`** — write-through projector maintaining per-day per-device aggregates from `DeviceEvents` + `ActivitySnapshots`.
- **`DataRetentionService`** — prunes per-minute detail (`ActivitySnapshots` + `ActivityProjections`) older than the user's retention setting. Invariants: never runs before the daily-stats backfill marker exists, drains the projector first, and deletes snapshots **before** their projection checkpoints.

### Data model (five tables + `AppMeta`)

- **`DeviceEvents`** — immutable append-only lifecycle log; the source of truth for connection history. Never pruned.
- **`Devices`** — mutable fast-read snapshot for the UI (derived from the event log).
- **`ActivitySnapshots`** — immutable per-(device, minute) input buckets. Pruned by retention.
- **`DailyDeviceStats`** — mutable per-day aggregates derived from the two above; the sole record of days whose raw minutes were pruned.
- **`ActivityProjections`** — per-(device, minute) checkpoints marking snapshots already projected into `DailyDeviceStats`; pruned together with snapshots.
- **`AppMeta`** — raw key/value table (outside EF) for markers like the one-time backfill timestamp.

DB lives at `%AppData%\KeyPulse Signal\keypulse-data.db` (Release) or `...\Test\keypulse-data.db` (Debug) — Debug and Release use **separate databases**.

## Conventions that bite

- **`IsConnected` ≠ `IsActive`**: `IsConnected` = device has an open session (`SessionStartedAt.HasValue`); `IsActive` = transient raw-input hold-state for highlighting. Don't conflate them.
- **Time helpers in `TimeFormatter`**: `NormalizeUtcMinute` (UTC, used by the daily-stats projector) and `TruncateToMinute` (preserves `DateTimeKind`, used by `RawInputService` for local-time buckets) are **not interchangeable**. Use `ToLocalDay()` rather than rolling your own date conversion.
- **Dispatcher safety**: services with background callbacks that touch UI-bound collections (`UsbMonitorService`, `RawInputService`) must guard with `ShutdownDispose.IsDispatcherUsable(dispatcher)` before `Invoke`/`BeginInvoke`. Pure services (`DataService`, `DailyStatsService`, helpers) don't need this.
- **Logging in helpers**: pure helpers (`RelayCommand`, `ObservableObject`) stay log-free; helpers touching FS/PowerShell/SetupAPI/crash-recovery should log failures. `TimeFormatter` is otherwise log-free except `ToRelativeTime`, which logs an error on a future timestamp (a clock-skew/logic bug). **Log messages state the condition in domain terms and must not leak internal code details** (method/class names) — e.g. `ToRelativeTime` logs `"Future timestamp received"`, not its own name.
- **`ObservableObject`** (in `Helpers/`) auto-marshals `PropertyChanged` to the UI thread.
- **View-model lifetime**: views are instantiated **once** in `MainWindow.xaml` and survive tab switches, so their DI-transient view-models effectively live for the process. State they expose must be kept live via event subscriptions, not constructor snapshots; `Dispose()` unsubscriptions exist for correctness on shutdown paths.

## Workflow Orchestration

### 1. Plan Mode Default
- Enter plan mode for ANY non-trivial task (3+ steps or architectural decisions)
- If something goes sideways, STOP and re-plan immediately - don't keep pushing
- Use plan mode for verification steps, not just building
- Write detailed specs upfront to reduce ambiguity

### 2. Subagent Strategy
- Use subagents liberally to keep main context window clean
- Offload research, exploration, and parallel analysis to subagents
- For complex problems, throw more compute at it via subagents
- One task per subagent for focused execution

### 3. Self-Improvement Loop
- After ANY correction from the user: update `tasks/lessons.md` with the pattern
- Write rules for yourself that prevent the same mistake
- Ruthlessly iterate on these lessons until mistake rate drops
- Review lessons at session start for relevant project

### 4. Verification Before Done
- Never mark a task complete without proving it works
- Diff behavior between main and your changes when relevant
- Ask yourself: "Would a staff engineer approve this?"
- Run tests, check logs, demonstrate correctness

### 5. Demand Elegance (Balanced)
- For non-trivial changes: pause and ask "is there a more elegant way?"
- If a fix feels hacky: "Knowing everything I know now, implement the elegant solution"
- Skip this for simple, obvious fixes - don't over-engineer
- Challenge your own work before presenting it

### 6. Autonomous Bug Fixing
- When given a bug report: just fix it. Don't ask for hand-holding
- Point at logs, errors, failing tests - then resolve them
- Zero context switching required from the user
- Go fix failing CI tests without being told how

## Task Management

1. **Only When Requested**: Create a TODO file only when the user explicitly requests one. TODOs are
   reserved for long tasks that need written steps. When requested, use `tasks/todo<TaskName>.md` with checkable items.
   `<TaskName>` is PascalCase and names the task, e.g. `tasks/todoProjectionWatermark.md`.
2. **One File Per Task**: Never write to a bare `tasks/todo.md`, and never overwrite or repurpose an
   existing todo file. `tasks/` is gitignored, so an overwrite is unrecoverable from git. If a plan
   seems obsolete, archive it rather than replacing it.
3. **Verify Plan**: Follow the user's requested review process for an explicitly requested TODO.
4. **Track Progress**: For an explicitly requested TODO, mark items complete as you go
5. **Explain Changes**: High-level summary at each step
6. **Document Results**: If the user requested a TODO, add a review section to it
7. **Archive When Done**: Move requested TODOs to `tasks/completed/`, keeping the same name
8. **Capture Lessons**: Update `tasks/lessons.md` after corrections

## Core Principles

- **Simplicity First**: Make every change as simple as possible. Impact minimal code.
- **No Laziness**: Find root causes. No temporary fixes. Senior developer standards.
- **Minimal Impact**: Changes should only touch what's necessary. Avoid introducing bugs.
- **Concise Comments**: Keep comments short — no more than 2-3 sentences. Explain the non-obvious "why", not the "what"; let the code speak for itself.
