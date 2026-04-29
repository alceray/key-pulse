using System.Diagnostics;
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

    public sealed class DashboardEventQueryResult
    {
        public required IReadOnlyList<DeviceEvent> DeviceEvents { get; init; }
        public required IReadOnlyList<DeviceEvent> AppLifecycleEvents { get; init; }
    }

    public DataService(IDbContextFactory<ApplicationDbContext> factory)
    {
        _factory = factory;
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        var stopwatch = Stopwatch.StartNew();
        using var ctx = _factory.CreateDbContext();
        try
        {
            var appliedMigrations = ctx.Database.GetAppliedMigrations().ToList();
            var pendingMigrations = ctx.Database.GetPendingMigrations().ToList();
            Log.Information(
                "Database migration status: Applied={AppliedCount}, Pending={PendingCount}",
                appliedMigrations.Count,
                pendingMigrations.Count
            );

            if (pendingMigrations.Count > 0)
            {
                Log.Information("Pending migrations: {PendingMigrations}", string.Join(", ", pendingMigrations));
                var backupPath = BackupDatabaseBeforeMigration(ctx);
                if (!string.IsNullOrWhiteSpace(backupPath))
                    Log.Information("Created pre-migration backup at {BackupPath}", backupPath);
            }

            Log.Information("Applying database migrations");
            ctx.Database.Migrate();
            Log.Information("Enabling SQLite WAL mode");
            ctx.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
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

    public void SaveDevice(Device device)
    {
        try
        {
            using var ctx = _factory.CreateDbContext();
            var existing = ctx.Devices.SingleOrDefault(d => d.DeviceId == device.DeviceId);
            if (existing != null)
                ctx.Entry(existing).CurrentValues.SetValues(device);
            else
                ctx.Devices.Add(device);
            ctx.SaveChanges();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ERROR in SaveDevice for DeviceId={DeviceId}", device.DeviceId);
        }
    }

    public IReadOnlyCollection<DeviceEvent> GetAllDeviceEvents()
    {
        using var ctx = _factory.CreateDbContext();
        return ctx.DeviceEvents.ToList().AsReadOnly();
    }

    /// <summary>
    /// Returns dashboard-ready event sets ordered up to the requested end time.
    /// </summary>
    public DashboardEventQueryResult GetDashboardEvents(DateTime to)
    {
        using var ctx = _factory.CreateDbContext();

        var allEvents = ctx
            .DeviceEvents.Where(e => e.EventTime <= to)
            .OrderBy(e => e.EventTime)
            .ThenBy(e => e.DeviceEventId)
            .ToList();

        var deviceEvents = allEvents.Where(e => e.DeviceId != "" && !e.EventType.IsAppEvent()).ToList();
        var appLifecycleEvents = allEvents
            .Where(e => e.EventType == EventTypes.AppStarted || e.EventType == EventTypes.AppEnded)
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

    public IReadOnlyCollection<DeviceEvent> GetEventsFromLastCompletedSession()
    {
        using var ctx = _factory.CreateDbContext();

        var lastAppEnded = ctx
            .DeviceEvents.Where(e => e.EventType == EventTypes.AppEnded)
            .OrderByDescending(e => e.DeviceEventId)
            .Select(e => (int?)e.DeviceEventId)
            .FirstOrDefault();

        if (!lastAppEnded.HasValue)
            return [];

        var lastAppStarted = ctx
            .DeviceEvents.Where(e => e.EventType == EventTypes.AppStarted && e.DeviceEventId < lastAppEnded.Value)
            .OrderByDescending(e => e.DeviceEventId)
            .Select(e => (int?)e.DeviceEventId)
            .FirstOrDefault();

        if (!lastAppStarted.HasValue)
            return [];

        return ctx
            .DeviceEvents.Where(e => e.DeviceEventId > lastAppStarted.Value && e.DeviceEventId < lastAppEnded.Value)
            .OrderBy(e => e.DeviceEventId)
            .ToList()
            .AsReadOnly();
    }

    public void SaveDeviceEvent(DeviceEvent deviceEvent)
    {
        try
        {
            using var ctx = _factory.CreateDbContext();
            ctx.DeviceEvents.Add(deviceEvent);
            ctx.SaveChanges();
        }
        catch (DbUpdateException ex)
        {
            Log.Warning(
                ex,
                "Duplicate DeviceEvent skipped for DeviceId={DeviceId}, EventType={EventType}, EventTime={EventTime}",
                deviceEvent.DeviceId,
                deviceEvent.EventType,
                deviceEvent.EventTime
            );
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ERROR in AddDeviceEvent for DeviceId={DeviceId}", deviceEvent.DeviceId);
        }
    }

    public void SaveActivitySnapshots(IEnumerable<ActivitySnapshot> snapshots)
    {
        try
        {
            using var ctx = _factory.CreateDbContext();
            foreach (var snapshot in snapshots)
            {
                var existing = ctx.ActivitySnapshots.SingleOrDefault(s =>
                    s.DeviceId == snapshot.DeviceId && s.Minute == snapshot.Minute
                );
                if (existing != null)
                {
                    // Update existing snapshot (merge activity)
                    existing.Keystrokes += snapshot.Keystrokes;
                    existing.MouseClicks += snapshot.MouseClicks;
                    existing.MouseMovementSeconds = Math.Max(
                        existing.MouseMovementSeconds,
                        snapshot.MouseMovementSeconds
                    );
                }
                else
                {
                    // Insert new snapshot
                    ctx.ActivitySnapshots.Add(snapshot);
                }
            }

            ctx.SaveChanges();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ERROR in SaveActivitySnapshots");
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
    /// Recomputes total usage for a device from the event log.
    /// Accepts an open context so callers sharing a unit of work can avoid extra round-trips.
    /// </summary>
    private static TimeSpan ComputeTotalUsage(ApplicationDbContext ctx, string deviceId)
    {
        var totalUsage = TimeSpan.Zero;
        DateTime? lastStartTime = null;
        var events = ctx.DeviceEvents.Where(e => e.DeviceId == deviceId).OrderBy(e => e.DeviceEventId).ToList();

        foreach (var deviceEvent in events)
            if (deviceEvent.EventType.IsOpeningEvent())
            {
                lastStartTime = deviceEvent.EventTime;
            }
            else if (deviceEvent.EventType.IsClosingEvent() && lastStartTime.HasValue)
            {
                totalUsage += deviceEvent.EventTime - lastStartTime.Value;
                lastStartTime = null;
            }

        return totalUsage;
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
        using var ctx = _factory.CreateDbContext();

        var lastAppEvent = ctx
            .DeviceEvents.Where(e => e.EventType == EventTypes.AppStarted || e.EventType == EventTypes.AppEnded)
            .OrderByDescending(e => e.DeviceEventId)
            .FirstOrDefault();

        if (lastAppEvent == null || lastAppEvent.EventType != EventTypes.AppStarted)
        {
            Log.Information("RecoverFromCrash skipped; no orphaned app session detected");
            stopwatch.Stop();
            return;
        }

        Log.Warning(
            "RecoverFromCrash detected unclean shutdown; last AppStarted at {OrphanedSessionStart}",
            lastAppEvent.EventTime
        );

        // Use the last heartbeat as the crash time if it's more recent than the session start.
        // Falls back to the session start timestamp if no heartbeat is available.
        var orphanedSessionStart = lastAppEvent.EventTime;
        var heartbeatTime = HeartbeatFile.Read();
        var crashTime =
            heartbeatTime.HasValue && heartbeatTime.Value > orphanedSessionStart
                ? heartbeatTime.Value
                : orphanedSessionStart;

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
            ctx.DeviceEvents.Add(
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

        ctx.DeviceEvents.Add(new DeviceEvent { EventType = EventTypes.AppEnded, EventTime = crashTime });

        ctx.SaveChanges();
        stopwatch.Stop();

        Log.Information(
            "RecoverFromCrash backfilled AppEnded and {ConnectionEndedCount} ConnectionEnded events at {CrashTime}; devices affected: {AffectedDeviceIds}; duration: {ElapsedMs}ms",
            unbalancedDeviceIds.Count,
            crashTime,
            string.Join(", ", unbalancedDeviceIds),
            stopwatch.ElapsedMilliseconds
        );
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
            Log.Information("Rebuilding device snapshots for {DeviceCount} devices", devices.Count);
            foreach (var device in devices)
            {
                device.TotalUsage = ComputeTotalUsage(ctx, device.DeviceId);
                device.SessionStartedAt = null;
                device.LastSeenAt ??= GetLastDeviceEvent(device.DeviceId)?.EventTime;
            }

            ctx.SaveChanges();
            stopwatch.Stop();
            Log.Information("RebuildDeviceSnapshots completed in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ERROR in RebuildDeviceSnapshots");
        }
    }
}
