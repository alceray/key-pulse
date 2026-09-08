using System.Diagnostics;
using System.Globalization;
using System.IO;
using KeyPulse.Configuration;
using KeyPulse.Data;
using KeyPulse.Helpers;
using KeyPulse.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace KeyPulse.Services;

public class DataService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory;
    private readonly DailyStatsService _dailyStats;
    private readonly DatabaseInstanceLock _databaseInstanceLock;

    private readonly object _deviceWriteLock = new();

    public sealed class DashboardEventQueryResult
    {
        public required IReadOnlyList<DeviceEvent> DeviceEvents { get; init; }
        public required IReadOnlyList<DeviceEvent> AppLifecycleEvents { get; init; }
    }

    public sealed class DashboardDeviceQueryResult
    {
        public required IReadOnlyList<Device> Devices { get; init; }
        public required IReadOnlyList<Device> TopKeyboardsByConnectionSeconds { get; init; }
        public required IReadOnlyList<Device> TopMiceByConnectionSeconds { get; init; }
    }

    public DataService(
        IDbContextFactory<ApplicationDbContext> factory,
        DailyStatsService dailyStats,
        DatabaseInstanceLock databaseInstanceLock
    )
    {
        _factory = factory;
        _dailyStats = dailyStats;
        _databaseInstanceLock = databaseInstanceLock;
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        var stopwatch = Stopwatch.StartNew();
        Log.Information("Database initialization started");
        using var ctx = _factory.CreateDbContext();
        try
        {
            // Startup normally holds this already, so this only covers a context that gets there first.
            _databaseInstanceLock.Acquire(ctx);
            var appliedMigrations = ctx.Database.GetAppliedMigrations().ToList();
            var pendingMigrations = ctx.Database.GetPendingMigrations().ToList();
            Log.Information(
                "Migration status: Applied={AppliedCount}, Pending={PendingCount}",
                appliedMigrations.Count,
                pendingMigrations.Count
            );

            if (pendingMigrations.Count > 0)
            {
                var backupPath = ctx.Database.IsSqlite() ? BackupDatabaseBeforeMigration(ctx) : null;
                if (!string.IsNullOrWhiteSpace(backupPath))
                    Log.Debug("Created pre-migration backup at {BackupPath}", backupPath);
                Log.Debug("Applying pending migrations: {PendingMigrations}", string.Join(", ", pendingMigrations));
            }

            ctx.Database.Migrate();
            AppMetaStore.EnsureTable(ctx);
            if (ctx.Database.IsSqlite())
            {
                DatabaseMigrations.RunAll(ctx);
                ctx.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
                SqliteHistoryFile.PruneBackups(ctx.Database.GetDbConnection().DataSource);
            }
            stopwatch.Stop();
            Log.Information("Database initialization completed in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log.Error(ex, "Database initialization failed after {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    private static string? BackupDatabaseBeforeMigration(ApplicationDbContext ctx)
    {
        try
        {
            var connection = ctx.Database.GetDbConnection();
            var databasePath = connection.DataSource;
            if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
                return null;

            var databaseDirectory = Path.GetDirectoryName(databasePath);
            if (string.IsNullOrWhiteSpace(databaseDirectory))
                return null;

            var backupDirectory = Path.Combine(databaseDirectory, AppConstants.Paths.DatabaseBackupsDirectoryName);
            Directory.CreateDirectory(backupDirectory);

            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var baseName = Path.GetFileNameWithoutExtension(databasePath);
            var extension = Path.GetExtension(databasePath);
            var backupFileName = $"{baseName}-{timestamp}{AppConstants.Paths.PreMigrationBackupSuffix}{extension}";
            var backupPath = Path.Combine(backupDirectory, backupFileName);

            File.Copy(databasePath, backupPath, overwrite: false);
            CopySidecarIfExists(databasePath + "-wal", backupPath + "-wal");
            CopySidecarIfExists(databasePath + "-shm", backupPath + "-shm");
            return backupPath;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to create pre-migration database backup");
            return null;
        }
    }

    private static void CopySidecarIfExists(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
            return;

        File.Copy(sourcePath, destinationPath, overwrite: true);
    }

    public Device? GetDevice(string deviceId)
    {
        using var ctx = _factory.CreateDbContext();
        return ctx.Devices.Find(deviceId);
    }

    public IReadOnlyCollection<Device> GetAllDevices()
    {
        using var ctx = _factory.CreateDbContext();
        return ctx.Devices.ToList().AsReadOnly();
    }

    public DashboardDeviceQueryResult GetDashboardDevices()
    {
        using var ctx = _factory.CreateDbContext();
        var devices = ctx
            .Devices.Where(d => !d.IsHiddenFromDisplay)
            .OrderBy(d => d.DeviceType)
            .ThenBy(d => d.DeviceName)
            .ToList();

        return new DashboardDeviceQueryResult
        {
            Devices = devices.AsReadOnly(),
            TopKeyboardsByConnectionSeconds = GetTopDevicesByConnectionSeconds(devices, DeviceTypes.Keyboard)
                .AsReadOnly(),
            TopMiceByConnectionSeconds = GetTopDevicesByConnectionSeconds(devices, DeviceTypes.Mouse).AsReadOnly(),
        };
    }

    private static List<Device> GetTopDevicesByConnectionSeconds(IEnumerable<Device> devices, DeviceTypes deviceType)
    {
        return devices
            .Where(d => d.DeviceType == deviceType)
            .OrderByDescending(d => d.TotalConnectionSeconds)
            .ThenBy(d => d.DeviceName)
            .Take(3)
            .ToList();
    }

    public void SaveDevice(Device device)
    {
        try
        {
            // SaveDevice runs on several threads (WMI worker threads and the UI thread), and the
            // existence check and insert are not atomic across separate connections. The lock serializes
            // them so a later caller sees the earlier committed row and updates it rather than racing to
            // re-insert the primary key, which would fail with a unique-constraint violation.
            lock (_deviceWriteLock)
            {
                using var ctx = _factory.CreateDbContext();
                var existing = ctx.Devices.SingleOrDefault(d => d.DeviceId == device.DeviceId);
                if (existing != null)
                    ctx.Entry(existing).CurrentValues.SetValues(device);
                else
                    ctx.Devices.Add(device);
                ctx.SaveChanges();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save device {DeviceId}", device.DeviceId);
        }
    }

    public bool SetDeviceHiddenFromDisplay(string deviceId, bool isHiddenFromDisplay)
    {
        try
        {
            using var ctx = _factory.CreateDbContext();
            var device = ctx.Devices.SingleOrDefault(d => d.DeviceId == deviceId);
            if (device == null)
                return false;

            device.IsHiddenFromDisplay = isHiddenFromDisplay;
            ctx.SaveChanges();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to update display visibility for {DeviceId}", deviceId);
            return false;
        }
    }

    public bool SetDeviceType(string deviceId, DeviceTypes deviceType)
    {
        try
        {
            lock (_deviceWriteLock)
            {
                using var ctx = _factory.CreateDbContext();
                var device = ctx.Devices.SingleOrDefault(d => d.DeviceId == deviceId);
                if (device == null)
                    return false;

                device.DeviceType = deviceType;
                ctx.SaveChanges();
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to update device type for {DeviceId}", deviceId);
            return false;
        }
    }

    public IReadOnlyCollection<DeviceEvent> GetAllDeviceEvents()
    {
        using var ctx = _factory.CreateDbContext();
        return ctx.DeviceEvents.ToList().AsReadOnly();
    }

    /// <summary>
    /// Returns dashboard-ready event sets bounded to the requested range.
    /// When <paramref name="from"/> is provided, includes pre-range seed events so that
    /// connection-state and app-running reconstruction is correct: the last opening event
    /// per device before <paramref name="from"/>, and the last AppStarted before <paramref name="from"/>.
    /// </summary>
    public DashboardEventQueryResult GetDashboardEvents(DateTime? from, DateTime to)
    {
        using var ctx = _factory.CreateDbContext();

        var inRange = ctx
            .DeviceEvents.Where(e => e.EventTime <= to && (!from.HasValue || e.EventTime >= from.Value))
            .OrderBy(e => e.EventTime)
            .ThenBy(e => e.DeviceEventId)
            .ToList();

        var seedDeviceEvents = new List<DeviceEvent>();
        var seedAppEvents = new List<DeviceEvent>();

        if (from.HasValue)
        {
            var fromValue = from.Value;

            // Seed: last opening event per device strictly before `from`, but only if no closing event
            // has occurred between that opening and `from` (i.e. the device was still connected at `from`).
            // Single query: take the latest opening-or-closing event before `from` for each device, then
            // keep only those whose latest pre-range state was open.
            var preRangeLatestPerDevice = ctx
                .DeviceEvents.Where(e =>
                    e.EventTime < fromValue
                    && e.DeviceId != ""
                    && (
                        e.EventType == EventTypes.ConnectionStarted
                        || e.EventType == EventTypes.Connected
                        || e.EventType == EventTypes.ConnectionEnded
                        || e.EventType == EventTypes.Disconnected
                    )
                )
                .GroupBy(e => e.DeviceId)
                .Select(g => g.OrderByDescending(e => e.EventTime).ThenByDescending(e => e.DeviceEventId).First())
                .ToList();

            seedDeviceEvents.AddRange(
                preRangeLatestPerDevice.Where(e =>
                    e.EventType == EventTypes.ConnectionStarted || e.EventType == EventTypes.Connected
                )
            );

            // Seed: last AppStarted strictly before `from` if app was still running at `from`.
            var lastAppLifecycle = ctx
                .DeviceEvents.Where(e =>
                    e.EventTime < fromValue
                    && (e.EventType == EventTypes.AppStarted || e.EventType == EventTypes.AppEnded)
                )
                .OrderByDescending(e => e.EventTime)
                .ThenByDescending(e => e.DeviceEventId)
                .FirstOrDefault();

            if (lastAppLifecycle != null && lastAppLifecycle.EventType == EventTypes.AppStarted)
                seedAppEvents.Add(lastAppLifecycle);
        }

        var deviceEvents = seedDeviceEvents
            .Concat(inRange.Where(e => e.DeviceId != "" && !e.EventType.IsAppEvent()))
            .OrderBy(e => e.EventTime)
            .ThenBy(e => e.DeviceEventId)
            .ToList();

        var appLifecycleEvents = seedAppEvents
            .Concat(inRange.Where(e => e.EventType == EventTypes.AppStarted || e.EventType == EventTypes.AppEnded))
            .OrderBy(e => e.EventTime)
            .ThenBy(e => e.DeviceEventId)
            .ToList();

        return new DashboardEventQueryResult
        {
            DeviceEvents = deviceEvents.AsReadOnly(),
            AppLifecycleEvents = appLifecycleEvents.AsReadOnly(),
        };
    }

    public DeviceEvent? GetLastDeviceEvent(string? deviceId = null)
    {
        using var ctx = _factory.CreateDbContext();
        var query = ctx.DeviceEvents.AsQueryable();

        if (!string.IsNullOrWhiteSpace(deviceId))
            query = query.Where(e => e.DeviceId == deviceId);

        return query.OrderByDescending(e => e.DeviceEventId).FirstOrDefault();
    }

    /// <summary>
    /// Device IDs with a ConnectionEnded in the last completed app session (one query; session bounds
    /// are inline subqueries, so the set is empty when there is no completed session). Computed once at
    /// startup so each ConnectionStarted is a set lookup rather than a per-device query.
    /// </summary>
    public IReadOnlySet<string> DevicesWithConnectionEndedInLastSession()
    {
        using var ctx = _factory.CreateDbContext();
        var appEnded = ctx.DeviceEvents.Where(e => e.EventType == EventTypes.AppEnded);

        return ctx
            .DeviceEvents.Where(e =>
                e.EventType == EventTypes.ConnectionEnded
                && e.DeviceId != ""
                && e.DeviceEventId < appEnded.Max(a => (int?)a.DeviceEventId)
                && e.DeviceEventId
                    > ctx.DeviceEvents.Where(s =>
                            s.EventType == EventTypes.AppStarted
                            && s.DeviceEventId < appEnded.Max(a => (int?)a.DeviceEventId)
                        )
                        .Max(s => (int?)s.DeviceEventId)
            )
            .Select(e => e.DeviceId)
            .Distinct()
            .ToHashSet();
    }

    public void SaveDeviceEvent(ApplicationDbContext ctx, DeviceEvent deviceEvent)
    {
        ctx.DeviceEvents.Add(deviceEvent);
        ctx.SaveChanges();
        _dailyStats.ApplyDeviceEvent(ctx, deviceEvent);
    }

    public void SaveDeviceEvent(DeviceEvent deviceEvent)
    {
        try
        {
            using var ctx = _factory.CreateDbContext();
            ctx.DeviceEvents.Add(deviceEvent);
            ctx.SaveChanges();
            _dailyStats.ApplyDeviceEvent(ctx, deviceEvent);
        }
        catch (DbUpdateException ex)
        {
            Log.Warning(
                ex,
                "Duplicate lifecycle event skipped for DeviceId={DeviceId}, EventType={EventType}, EventTime={EventTime}",
                deviceEvent.DeviceId,
                deviceEvent.EventType,
                deviceEvent.EventTime
            );
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save lifecycle event for {DeviceId}", deviceEvent.DeviceId);
        }
    }

    public void SaveActivitySnapshots(IEnumerable<ActivitySnapshot> snapshots)
    {
        try
        {
            using var ctx = _factory.CreateDbContext();
            var devicesById = ctx.Devices.ToDictionary(d => d.DeviceId, d => d);
            foreach (var snapshot in snapshots)
            {
                if (!devicesById.TryGetValue(snapshot.DeviceId, out var device))
                    continue;

                // Check pending (Local) rows first so duplicates within the same batch merge into the
                // already-added snapshot instead of colliding on the unique (DeviceId, Minute) index.
                var existing =
                    ctx.ActivitySnapshots.Local.FirstOrDefault(s =>
                        s.DeviceId == snapshot.DeviceId && s.Minute == snapshot.Minute
                    )
                    ?? ctx.ActivitySnapshots.SingleOrDefault(s =>
                        s.DeviceId == snapshot.DeviceId && s.Minute == snapshot.Minute
                    );
                var snapshotChanged = false;

                if (existing != null)
                {
                    // Update existing snapshot (merge activity)
                    var previousMovementSeconds = existing.MouseMovementSeconds;
                    existing.Keystrokes += snapshot.Keystrokes;
                    existing.MouseClicks += snapshot.MouseClicks;
                    existing.MouseMovementSeconds = Math.Max(
                        existing.MouseMovementSeconds,
                        snapshot.MouseMovementSeconds
                    );
                    // Active seconds is a derived activity measure, not an input count, so it is not
                    // folded into TotalInputCount below.
                    existing.ActiveSeconds = Math.Max(existing.ActiveSeconds, snapshot.ActiveSeconds);

                    var movementDelta = existing.MouseMovementSeconds - previousMovementSeconds;
                    var keyDelta = snapshot.Keystrokes + snapshot.MouseClicks;
                    snapshotChanged = keyDelta > 0 || movementDelta > 0;

                    device.TotalInputCount += keyDelta + movementDelta;
                }
                else
                {
                    // Insert new snapshot
                    ctx.ActivitySnapshots.Add(snapshot);

                    var inputDelta = snapshot.Keystrokes + snapshot.MouseClicks + snapshot.MouseMovementSeconds;
                    snapshotChanged = inputDelta > 0;
                    device.TotalInputCount += inputDelta;
                }

                if (snapshotChanged)
                    _dailyStats.MarkActivitySnapshotWritten(snapshot.Minute);
            }

            ctx.SaveChanges();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save activity snapshots");
        }
    }

    public IReadOnlyCollection<ActivitySnapshot> GetActivitySnapshots(
        string? deviceId = null,
        DateTime? from = null,
        DateTime? to = null
    )
    {
        using var ctx = _factory.CreateDbContext();
        var query = ctx.ActivitySnapshots.AsQueryable();

        if (!string.IsNullOrWhiteSpace(deviceId))
            query = query.Where(s => s.DeviceId == deviceId);
        if (from.HasValue)
            query = query.Where(s => s.Minute >= from.Value);
        if (to.HasValue)
            query = query.Where(s => s.Minute <= to.Value);

        return query.OrderBy(s => s.Minute).ToList().AsReadOnly();
    }

    /// <summary>
    /// Recomputes total connection seconds for a device from the event log.
    /// Accepts an open context so callers sharing a unit of work can avoid extra round-trips.
    /// </summary>
    private static long ComputeConnectionSeconds(ApplicationDbContext ctx, string deviceId)
    {
        var connectionSeconds = 0L;
        DateTime? lastStartTime = null;
        var events = ctx.DeviceEvents.Where(e => e.DeviceId == deviceId).OrderBy(e => e.DeviceEventId).ToList();

        foreach (var deviceEvent in events)
            if (deviceEvent.EventType.IsOpeningEvent())
            {
                lastStartTime = deviceEvent.EventTime;
            }
            else if (deviceEvent.EventType.IsClosingEvent() && lastStartTime.HasValue)
            {
                connectionSeconds += (long)(deviceEvent.EventTime - lastStartTime.Value).TotalSeconds;
                lastStartTime = null;
            }

        return connectionSeconds;
    }

    /// <summary>
    /// Recomputes total input count from the daily aggregates plus any snapshots not yet projected
    /// into them. Every snapshot is counted exactly once through one of the two terms, so the total
    /// stays exact even after old snapshots are pruned by retention.
    /// </summary>
    private static long ComputeTotalInputCount(ApplicationDbContext ctx, string deviceId)
    {
        var projected =
            ctx.DailyDeviceStats.Where(d => d.DeviceId == deviceId)
                .Select(d => (long?)(d.Keystrokes + d.MouseClicks + d.MouseMovementSeconds))
                .Sum() ?? 0L;

        var unprojected =
            ctx.ActivitySnapshots.Where(s =>
                    s.DeviceId == deviceId
                    && !ctx.ActivityProjections.Any(p => p.DeviceId == s.DeviceId && p.Minute == s.Minute)
                )
                .Select(s => (long?)s.Keystrokes + s.MouseClicks + s.MouseMovementSeconds)
                .Sum() ?? 0L;

        return projected + unprojected;
    }

    /// <summary>
    /// Counts the distinct local days this device was connected. DailyDeviceStats holds one row per
    /// (day, device) and is never pruned, so the count stays exact across the device's whole history.
    /// </summary>
    private static int ComputeDaysConnected(ApplicationDbContext ctx, string deviceId)
    {
        return ctx.DailyDeviceStats.Count(d => d.DeviceId == deviceId && d.ConnectionSeconds > 0);
    }

    /// <summary>
    /// Checks if the previous session ended cleanly (AppEnded was written).
    /// If not (e.g., process was killed in the IDE or crashed), retroactively writes
    /// the missing AppEnded and ConnectionEnded events so the log stays consistent.
    /// Should be called before writing the new AppStarted event.
    /// </summary>
    public void RecoverFromCrash()
    {
        var stopwatch = Stopwatch.StartNew();
        Log.Information("Crash recovery check started");
        using var ctx = _factory.CreateDbContext();

        var lastAppEvent = ctx
            .DeviceEvents.Where(e => e.EventType == EventTypes.AppStarted || e.EventType == EventTypes.AppEnded)
            .OrderByDescending(e => e.DeviceEventId)
            .FirstOrDefault();

        if (lastAppEvent == null || lastAppEvent.EventType != EventTypes.AppStarted)
        {
            stopwatch.Stop();
            Log.Information("Crash recovery check passed in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
            return;
        }

        // Use the last heartbeat as the crash time if it's more recent than the session start.
        // Falls back to the session start timestamp if no heartbeat is available.
        var orphanedSessionStart = lastAppEvent.EventTime;
        var heartbeatTime = HeartbeatFile.Read();
        DateTime? heartbeatLocal = heartbeatTime.HasValue ? TimeFormatter.ToLocalTime(heartbeatTime.Value) : null;
        var crashTime =
            heartbeatLocal.HasValue && heartbeatLocal.Value > orphanedSessionStart
                ? heartbeatLocal.Value
                : orphanedSessionStart;

        Log.Warning(
            "Unclean shutdown detected: last session began at {OrphanedSessionStart} and crashed around {HeartbeatTime}",
            orphanedSessionStart.ToString(AppConstants.Date.DateFormat, CultureInfo.InvariantCulture),
            crashTime.ToString(AppConstants.Date.DateFormat, CultureInfo.InvariantCulture)
        );

        // Backfill ConnectionEnded for devices that have more opening than closing events.
        var orphanedSessionDeviceEvents = ctx
            .DeviceEvents.Where(e => e.DeviceId != "" && e.EventTime >= orphanedSessionStart)
            .ToList();

        var unbalancedDeviceIds = orphanedSessionDeviceEvents
            .GroupBy(e => e.DeviceId)
            .Where(g => g.Count(e => e.EventType.IsOpeningEvent()) > g.Count(e => e.EventType.IsClosingEvent()))
            .Select(g => g.Key)
            .ToList();

        foreach (var deviceId in unbalancedDeviceIds)
        {
            SaveDeviceEvent(
                ctx,
                new DeviceEvent
                {
                    DeviceId = deviceId,
                    EventType = EventTypes.ConnectionEnded,
                    EventTime = crashTime,
                }
            );

            var device = ctx.Devices.SingleOrDefault(d => d.DeviceId == deviceId);
            if (device != null)
                device.LastSeenAt = crashTime;
        }

        SaveDeviceEvent(ctx, new DeviceEvent { EventType = EventTypes.AppEnded, EventTime = crashTime });

        try
        {
            ctx.SaveChanges();
        }
        catch (DbUpdateException ex)
        {
            Log.Warning(ex, "Duplicate event skipped during crash recovery; proceeding");
        }

        Log.Debug(
            "Backfilled {ConnectionEndedCount} connection close events for {AffectedDeviceIds}",
            unbalancedDeviceIds.Count,
            string.Join(", ", unbalancedDeviceIds)
        );

        stopwatch.Stop();
        Log.Information("Crash recovery completed in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Rebuilds persisted device snapshots from the event log.
    /// DeviceEvents remain the source of truth; Device acts as the fast-read snapshot.
    /// </summary>
    public void RebuildDeviceSnapshots()
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            using var ctx = _factory.CreateDbContext();
            var devices = ctx.Devices.ToList();
            Log.Information("Device snapshot rebuild started for {DeviceCount} devices", devices.Count);
            foreach (var device in devices)
            {
                device.TotalConnectionSeconds = ComputeConnectionSeconds(ctx, device.DeviceId);
                device.TotalInputCount = ComputeTotalInputCount(ctx, device.DeviceId);
                device.DaysConnected = ComputeDaysConnected(ctx, device.DeviceId);
                device.SessionStartedAt = null;
                device.LastSeenAt ??= GetLastDeviceEvent(device.DeviceId)?.EventTime;
            }

            ctx.SaveChanges();
            stopwatch.Stop();
            Log.Information("Device snapshot rebuild completed in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Device snapshot rebuild failed");
        }
    }
}
