using System.Diagnostics;
using KeyPulse.Data;
using KeyPulse.Helpers;
using KeyPulse.Models;
using Microsoft.EntityFrameworkCore;

namespace KeyPulse.Services;

public class DataService
{
    private readonly IDbContextFactory<ApplicationDbContext> _factory;

    public DataService(IDbContextFactory<ApplicationDbContext> factory)
    {
        _factory = factory;
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var ctx = _factory.CreateDbContext();
        ctx.Database.Migrate();
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
            Debug.WriteLine($"ERROR in SaveDevice: {ex.Message}");
        }
    }

    public bool IsAnyDeviceActive()
    {
        using var ctx = _factory.CreateDbContext();
        // SessionStartedAt is mapped; IsActive is [NotMapped] and cannot be translated.
        return ctx.Devices.Any(d => d.SessionStartedAt != null);
    }

    public IReadOnlyCollection<DeviceEvent> GetAllDeviceEvents()
    {
        using var ctx = _factory.CreateDbContext();
        return ctx.DeviceEvents.ToList().AsReadOnly();
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
            Debug.WriteLine($"Duplicate DeviceEvent skipped: {ex.Message}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ERROR in AddDeviceEvent: {ex.Message}");
        }
    }

    public void SaveActivitySnapshots(IEnumerable<ActivitySnapshot> snapshots)
    {
        try
        {
            using var ctx = _factory.CreateDbContext();
            ctx.ActivitySnapshots.AddRange(snapshots);
            ctx.SaveChanges();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ERROR in SaveActivitySnapshots: {ex.Message}");
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
                lastStartTime = deviceEvent.Timestamp;
            else if (deviceEvent.EventType.IsClosingEvent() && lastStartTime.HasValue)
            {
                totalUsage += deviceEvent.Timestamp - lastStartTime.Value;
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
        using var ctx = _factory.CreateDbContext();

        var lastAppEvent = ctx
            .DeviceEvents.Where(e => e.EventType == EventTypes.AppStarted || e.EventType == EventTypes.AppEnded)
            .OrderByDescending(e => e.DeviceEventId)
            .FirstOrDefault();

        if (lastAppEvent == null || lastAppEvent.EventType != EventTypes.AppStarted)
            return;

        // Use the last heartbeat as the crash time if it's more recent than the session start.
        // Falls back to the session start timestamp if no heartbeat is available.
        var orphanedSessionStart = lastAppEvent.Timestamp;
        var heartbeatTime = HeartbeatFile.Read();
        var crashTime =
            heartbeatTime.HasValue && heartbeatTime.Value > orphanedSessionStart
                ? heartbeatTime.Value
                : orphanedSessionStart;

        // Backfill ConnectionEnded for devices that have more opening than closing events.
        var orphanedSessionDeviceEvents = ctx
            .DeviceEvents.Where(e => e.DeviceId != "" && e.Timestamp >= orphanedSessionStart)
            .ToList();

        var unbalancedDeviceIds = orphanedSessionDeviceEvents
            .GroupBy(e => e.DeviceId)
            .Where(g => g.Count(e => e.EventType.IsOpeningEvent()) > g.Count(e => e.EventType.IsClosingEvent()))
            .Select(g => g.Key)
            .ToList();

        foreach (var deviceId in unbalancedDeviceIds)
            ctx.DeviceEvents.Add(
                new DeviceEvent
                {
                    DeviceId = deviceId,
                    EventType = EventTypes.ConnectionEnded,
                    Timestamp = crashTime,
                }
            );

        ctx.DeviceEvents.Add(new DeviceEvent { EventType = EventTypes.AppEnded, Timestamp = crashTime });

        ctx.SaveChanges();
        Debug.WriteLine("RecoverFromCrash: wrote missing AppEnded and ConnectionEnded events.");
    }

    /// <summary>
    /// Rebuilds persisted device snapshots from the event log.
    /// DeviceEvents remain the source of truth; Device acts as the fast-read snapshot.
    /// </summary>
    public void RebuildDeviceSnapshots()
    {
        try
        {
            using var ctx = _factory.CreateDbContext();
            var devices = ctx.Devices.ToList();
            foreach (var device in devices)
            {
                device.TotalUsage = ComputeTotalUsage(ctx, device.DeviceId);
                device.SessionStartedAt = null;
            }
            ctx.SaveChanges();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ERROR in RebuildDeviceSnapshots: {ex.Message}");
        }
    }
}
