# KeyPulse Signal

**Know exactly which board or mouse you picked up today, and how hard you pushed it.**

KeyPulse Signal is a lightweight Windows desktop app built for keyboard and mouse enthusiasts who frequently rotate gear and want real data behind their daily drivers. It silently tracks every USB connection, counts every keystroke and click per device, and gives you a per-device breakdown by day. You always know which board carried the most load this week, or how long that new endgame actually spent on the desk.

![KeyPulse Signal dashboard showing all-time keyboard and mouse activity across connected USB devices](docs/images/dashboard-all-time.png)

## What It Tracks

- **Connection history**: when each external USB device was plugged in, for how long, and how many sessions.
- **Input activity**: per-device keystrokes, mouse clicks, and movement time captured silently in the background.
- **Daily summaries**: a calendar view showing which devices were active on any given day, with session counts, active time as a share of connected time, and an hour-by-hour activity breakdown that highlights your peak hour.
- **Live device state**: a device list showing what's connected right now, with real-time input counters ticking up as you type, alongside per-device lifetime totals like time connected and days connected.

## Features

- Automatically detects supported USB keyboards and mice as they come and go.
- Counts keystrokes, mouse clicks, and active mouse-movement seconds per device.
- Keeps connection history, sessions, daily totals, and lifetime stats in SQLite by default, with optional self-hosted PostgreSQL storage.
- Includes a dashboard, day-by-day calendar, and live device list.
- Runs in the system tray, supports session-only tracking pause, and can hide devices from presentation views without stopping their tracking.
- Keeps history across restarts and recovers unfinished sessions after an unexpected shutdown.

![KeyPulse Signal calendar with a day selected, showing per-device connection, activity, sessions, and hourly input](docs/images/calendar-day.png)

## Install and run

Download the latest installer from the [GitHub Releases page](https://github.com/alceray/keypulse-signal/releases). KeyPulse Signal runs on Windows 10 version 1607 or later. Windows 11 is recommended.

Framework-dependent builds require the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0). External USB keyboards and mice are supported. Built-in laptop keyboards and trackpads are not tracked.

## Develop locally

### Prerequisites

- Windows 10 or 11
- .NET 8 SDK
- Visual Studio 2022 or JetBrains Rider with WPF support

```powershell
dotnet restore
dotnet build -c Debug
dotnet run -c Debug
```

To test release behavior locally:

```powershell
dotnet run -c Release
```

Debug builds open a normal window. Release builds start in the system tray. Add `--tray` to force tray mode for any launch.

## Your data

SQLite is the recommended zero-configuration default. On first setup, or later in Settings, you can instead connect KeyPulse to a dedicated PostgreSQL database that you manage. KeyPulse stores PostgreSQL passwords in Windows Credential Manager and never silently falls back between databases.

KeyPulse records activity totals, not the content of your input. It does not record which keys you press, the text you type, mouse coordinates, or mouse paths.

| Build | Database location |
| --- | --- |
| Release | `%AppData%\KeyPulse Signal\keypulse-data.db` |
| Debug | `%AppData%\KeyPulse Signal\Test\keypulse-data.db` |

Debug and Release keep independent settings, credentials, SQLite files, and PostgreSQL databases. Provider changes copy the active history after restart, before monitoring begins. Switching to PostgreSQL asks you to confirm replacing any existing KeyPulse history there. Switching to SQLite verifies a fresh local database and saves the previous local history in `DbBackups`. Repeated switches are supported, and interrupted transfers resume safely.

The database stores device snapshots, connection events, minute-level activity snapshots, and daily aggregates. Retention settings only prune old minute-level detail. Your daily history and connection totals stay intact.

### Using PostgreSQL

KeyPulse creates neither the database nor the role. Both must already exist, because a connection cannot create the database it connects to. Create them once as a superuser:

```sql
CREATE ROLE keypulse LOGIN PASSWORD 'choose-a-password';
CREATE DATABASE keypulse_signal OWNER keypulse;
```

Those names are only the defaults shown in the form. Use a dedicated database and a role that can manage its KeyPulse tables in the `public` schema. Making the role the owner is the simplest way to grant that. First-run setup requires an empty destination. Later switches from Settings can replace existing KeyPulse history after confirmation. PostgreSQL 15 and later no longer let a non-owner create tables in `public`, so a role you do not own the database with also needs `GRANT CREATE, USAGE ON SCHEMA public`.

Then open Settings, choose PostgreSQL, and fill in the host, port, database, user, and password. Test confirms both that the role can sign in and that it can create the schema. Apply saves the password to Windows Credential Manager and stages the switch. The move itself runs on the next start, before monitoring begins, so restart the app to complete it.

If startup cannot complete a transfer, recovery offers retry and cancellation. An explicit **Use local without copying** option lets you leave an unavailable PostgreSQL database behind. Its history remains on the server, and the local history may be older or empty. A normal provider change always copies history.

Before switching to SQLite, disconnect the local SQLite database in tools such as Rider or DB Browser for SQLite. An idle connection can prevent replacing the file even when no query is running. If recovery reports that the local database is in use, disconnect those tools and choose **Retry** to finish copying history.

To run provider integration tests, set `KEYPULSE_TEST_POSTGRES_BIN` to the directory containing `initdb.exe` and `pg_ctl.exe`, then run `dotnet test "KeyPulse Signal.sln"`. The tests create a temporary PostgreSQL cluster on a free loopback port and remove it afterward. They do not connect to your configured database. Without that variable, PostgreSQL integration tests are skipped and SQLite tests still run.

Contributors who run both builds need a second database, since Debug and Release refuse to share one. Create `keypulse_signal_test` the same way and point the Debug build at it.

## How it works

KeyPulse uses Windows WMI to watch USB connection changes and Windows Raw Input to attribute typing and mouse activity to individual devices. EF Core with SQLite or PostgreSQL handles persistence. The WPF interface is backed by a small set of singleton services for monitoring, capture, timing, daily aggregation, retention, and application settings.

The project targets `.NET 8` and uses WPF, EF Core 9, SQLite/PostgreSQL, Serilog, OxyPlot, and `Microsoft.Extensions.DependencyInjection`.

## Technical architecture

The app keeps device capture, persistence, and presentation separate. Long-lived services are registered as singletons, while tab view-models are created when their views are opened.

| Component | Responsibility |
| --- | --- |
| `AppTimerService` | Owns the shared UI-thread timers for live updates, dashboard refreshes, daily work, and minute projections. |
| `UsbMonitorService` | Watches WMI device arrival and removal events, filters for supported HID devices, combines bursty interface events into one connection, and maintains the UI-bound device snapshot. |
| `RawInputService` | Runs a hidden `WM_INPUT` listener with `RIDEV_INPUTSINK`, captures input even in tray mode, and collects per-device minute buckets. |
| `DataService` | Applies provider-specific migrations, enables SQLite WAL mode where applicable, owns database operations, recovers from unclean shutdowns, and rebuilds device snapshots on startup. |
| `DailyStatsService` | Projects lifecycle events and closed activity minutes into durable per-device, per-day aggregates. It performs a one-time historical backfill, then reconciles missing aggregate rows on later starts. |
| `DataRetentionService` | Prunes old minute-level snapshots and projection checkpoints only after they have been reflected in daily aggregates. |

### Data flow

1. On startup, the app creates its service container, migrates the database, recovers unfinished sessions, and rebuilds the fast device snapshot.
2. `UsbMonitorService` scans currently connected devices, writes lifecycle events, then starts WMI watchers for later changes.
3. `RawInputService` maps Raw Input handles to device IDs and records keys, clicks, and movement-active seconds in one-minute buckets.
4. Closed buckets are saved as activity snapshots and projected into daily totals. The dashboard and calendar read those totals, while live input deltas keep today's UI current between flushes.
5. On a normal exit, the app flushes the partial minute and closes device and app sessions. Crash recovery covers the cases where Windows or a process termination prevents that cleanup.

### Persistence model

| Table | Purpose |
| --- | --- |
| `Devices` | Mutable device snapshot for fast UI reads, including name, type, visibility preference, and lifetime totals. |
| `DeviceEvents` | Append-only app and device lifecycle log. This is the source of truth for connection history. |
| `ActivitySnapshots` | Immutable per-device minute buckets for keystrokes, clicks, and mouse-movement seconds. |
| `DailyDeviceStats` | Durable per-device daily aggregates for connections and input activity. It keeps calendar history available after minute-level retention pruning. |
| `ActivityProjections` | Per-minute checkpoints that ensure a snapshot is projected into daily totals exactly once. |

Hidden devices are a presentation setting only. They continue to be monitored, captured, stored, and aggregated, but are filtered out of dashboard and calendar queries.

## Troubleshooting

- A second launch restores the existing app window. This is expected.
- If a build says the executable is locked, close the running KeyPulse instance and build again.
- If a device appears as `Unknown Device`, Windows did not provide a usable friendly name. You can rename it in the app.
- The Troubleshooting page includes searchable logs when you need a closer look.

## Project docs

| Document | Use it for |
| --- | --- |
| [AGENTS.md](AGENTS.md) | Architecture, data flow, and implementation conventions |
| [Production readiness plan](docs/PRODUCTION_READINESS_PLAN.md) | Hardening work and remaining production tasks |
| [Release process](docs/RELEASE_PROCESS.md) | Versioning, packaging, and release workflow |
| [Release checklist](docs/RELEASE_CHECKLIST.md) | Final validation before publishing |
| [Changelog](CHANGELOG.md) | What changed in each release |

## License

See [LICENSE.txt](LICENSE.txt).
