# KeyPulse Signal

**Know exactly which board or mouse you picked up today, and how hard you pushed it.**

KeyPulse Signal is a Windows desktop app that tracks how much you use each USB keyboard and mouse. Compare your daily drivers by connection time, keystrokes, clicks, and daily activity, with tracking that continues in the background while the app runs in your system tray.

![KeyPulse Signal dashboard showing all-time keyboard and mouse activity across connected USB devices](docs/images/dashboard-all-time.png)

## Get started

### Requirements

- Windows 10 version 1607 or later.
- An external USB keyboard or mouse. Built-in laptop keyboards and trackpads are not tracked.
- The [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) if you use a framework-dependent build.

### Install and launch

1. Download the latest installer from [GitHub Releases](https://github.com/alceray/keypulse-signal/releases).
2. Run the installer, then launch KeyPulse Signal.
3. Choose **SQLite** during setup for local storage with no database configuration. For a server database, see [PostgreSQL setup](#optional-postgresql-storage).
4. Connect your USB keyboard or mouse; supported devices are detected automatically.
5. Open KeyPulse from the system tray to explore your activity in the dashboard, calendar, and device list.

## Explore your activity

- **Dashboard:** compare device activity and connection time, from the last day to all time.
- **Calendar:** select a day to see devices used, sessions, connected time, active time, and your busiest hours.
- **Device list:** see connected devices, live input counters, lifetime connection time, and days connected.
- **Connection history:** see when each device connected and disconnected.
- **Activity totals:** track keystrokes, mouse clicks, and the number of seconds with mouse movement.

![KeyPulse Signal calendar with a day selected, showing per-device connection, activity, sessions, and hourly input](docs/images/calendar-day.png)

### Manage your devices

- Rename devices so your gear is easy to recognize.
- Right-click a device to correct its type to Keyboard, Mouse, or Other.
- Use the device list context menu to hide a device from the dashboard and calendar, or to unhide it later.
- Hidden devices keep tracking and remain accessible in the device list.
- Pause tracking for the current session when needed.

## Privacy and your data

KeyPulse saves activity counts and connection history. It does not save which keys you press, typed text, mouse coordinates, or mouse paths.

- **Local storage:** SQLite stores your database at `%AppData%\KeyPulse Signal\keypulse-data.db` by default.
- **Server storage:** optionally use a dedicated PostgreSQL database that you manage.
- **Passwords:** PostgreSQL passwords are stored in Windows Credential Manager.
- **History:** saved history carries across restarts, with recovery for unfinished sessions after an unexpected shutdown.
- **Retention:** remove old minute-by-minute detail while keeping daily history and connection totals.

## Troubleshooting

| Problem | What to do |
| --- | --- |
| No window after launch | Check the system tray. |
| Launching again opens the existing window | This is expected; only one KeyPulse instance runs at a time. |
| A device shows as Unknown Device | Windows did not provide a usable name. Rename it in the app. |
| A device has the wrong type | Right-click it in the device list and choose Keyboard, Mouse, or Other. |
| A device is missing from the dashboard or calendar | Check whether it has a Hidden badge in the device list, then use its context menu to unhide it. |
| A database connection or transfer fails | See [database recovery](#database-recovery) below. |
| You need more detail | Open the Troubleshooting page to search the logs. |

## Optional: PostgreSQL storage

SQLite needs no database setup. Choose PostgreSQL if you want to store history in a dedicated database that you manage.

### Create a database

1. Open a PostgreSQL administrator session.
2. Create a login and database using the SQL below. Replace the example password; you can also change the login and database names.

   ```sql
   CREATE ROLE keypulse LOGIN PASSWORD 'choose-a-password';
   CREATE DATABASE keypulse_signal OWNER keypulse;
   ```

3. Use an empty destination database for first-run setup.

- Making the login the database owner lets KeyPulse manage its tables.
- If you use a non-owner login, grant it permission to use and create tables in the `public` schema.

### Connect KeyPulse

1. Open **Settings** and choose **PostgreSQL** under storage, or select it during first-run setup.
2. Enter the host, port, database, user, and password.
3. Choose **Test** to check the connection and table-creation permissions.
4. In Settings, choose **Save and restart** and confirm any replacement of destination history.
5. Wait for the transfer to finish. The main window reopens after restart, including in tray mode.

### Change databases

1. Finish or cancel any pending database change.
2. In Settings, select the storage type you want. To move between PostgreSQL databases, choose **Edit** under storage.
3. Enter the destination details. Check the masked password field and update it if the destination uses a different password.
4. Choose **Test** for a PostgreSQL destination.
5. Choose **Save and restart**. Check the source and destination names before confirming replacement of destination history.
6. Follow the progress window through preparation, copying, verification, and activation.

- Database changes copy your current history before tracking resumes.
- Changing only the current database's username, password, or SSL mode updates the connection without replacing history.
- Your source PostgreSQL history stays on the server after a move.
- **Stop and exit** requests cancellation; unfinished changes stay pending for the next launch.
- Before switching to SQLite, disconnect the local database from tools such as Rider or DB Browser for SQLite.
- Switching to SQLite backs up the previous local database in `DbBackups`.
- KeyPulse keeps the three newest automatic SQLite backups, including import and migration backups.

### Database recovery

KeyPulse never silently switches databases when a connection fails. Startup recovery lets you retry a transfer or cancel an unfinished change.

| Situation | What to do |
| --- | --- |
| Source or destination login fails | Correct the affected **Source** or **Destination** login details when prompted. |
| The local SQLite database is in use | Disconnect it from database tools, then choose **Retry**. |
| You want to cancel an unfinished change | Use the recovery cancellation option to keep the source connection and password. |
| PostgreSQL is unavailable and you want to use local storage | Choose **Use local without copying**. Server history stays on the server; local history may be older or empty. |

## For contributors

### Build and run

- Use Windows 10 or 11 and the .NET 8 SDK.
- Use Visual Studio 2022 or JetBrains Rider with WPF support.

From the repository folder, restore, build, and launch a Debug build:

```powershell
dotnet restore
dotnet build -c Debug
dotnet run -c Debug
```

To test Release behavior:

```powershell
dotnet run -c Release
```

| Build | Startup behavior | SQLite database |
| --- | --- | --- |
| Debug | Opens a window | `%AppData%\KeyPulse Signal\Test\keypulse-data.db` |
| Release | Starts in the system tray | `%AppData%\KeyPulse Signal\keypulse-data.db` |

- Add `--tray` as an application argument to force tray mode.
- Debug and Release use separate settings, credentials, and databases.
- Create a second PostgreSQL database, such as `keypulse_signal_test`, for Debug builds.
- If the executable is locked during a build, close the running KeyPulse instance and build again.

### Run tests

Run the test suite from the repository folder:

```powershell
dotnet test "KeyPulse Signal.sln"
```

- To include PostgreSQL integration tests, set `KEYPULSE_TEST_POSTGRES_BIN` to the folder containing `initdb.exe` and `pg_ctl.exe`.
- PostgreSQL tests use a temporary cluster on a free loopback port and remove it afterward; they do not connect to your configured database.
- Without the environment variable, PostgreSQL integration tests are skipped; SQLite tests still run.

### Technical overview

- **Platform:** .NET 8 and WPF.
- **Device detection and activity:** Windows WMI and Windows Raw Input.
- **Storage:** EF Core 9 with SQLite or PostgreSQL.
- **Charts and logging:** OxyPlot and Serilog.
- **Architecture:** see [AGENTS.md](AGENTS.md) for service responsibilities, data flow, and implementation conventions.

## Project docs

- [Release process](docs/RELEASE_PROCESS.md): versioning, packaging, and deferred code signing.
- [Release checklist](docs/RELEASE_CHECKLIST.md): checks before publishing.
- [Changelog](CHANGELOG.md): changes in each release.

## License

See [LICENSE.txt](LICENSE.txt).
