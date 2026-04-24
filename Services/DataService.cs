using System.Diagnostics;
using KeyPulse.Data;
using KeyPulse.Helpers;
using KeyPulse.Models;
using Microsoft.EntityFrameworkCore;

namespace KeyPulse.Services;

public class DataService
{
    private readonly ApplicationDbContext _context;

    public DataService(ApplicationDbContext context)
    {
        _context = context;
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        _context.Database.Migrate();
    }

    public Device? GetDevice(string deviceId)
    {
        return _context.Devices.Find(deviceId);
    }

    public IReadOnlyCollection<Device> GetAllDevices()
    {
        return _context.Devices.ToList().AsReadOnly();
    }

    public void SaveDevice(Device device)
    {
        try
        {
            var existingDevice = _context.Devices.SingleOrDefault(d => d.DeviceId == device.DeviceId);
            if (existingDevice != null)
                _context.Entry(existingDevice).CurrentValues.SetValues(device);
            else
                _context.Devices.Add(device);
            _context.SaveChanges();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ERROR in SaveDevice: {ex.Message}");
        }
    }

    public bool IsAnyDeviceActive()
    {
        return _context.Devices.Any(d => d.IsActive);
    }

    public IReadOnlyCollection<DeviceEvent> GetAllDeviceEvents()
    {
        return _context.DeviceEvents.ToList().AsReadOnly();
    }

    public DeviceEvent? GetLastDeviceEvent(string? deviceId = null)
    {
        var query = _context.DeviceEvents.AsQueryable();

        if (!string.IsNullOrWhiteSpace(deviceId))
            query = query.Where(e => e.DeviceId == deviceId);

        return query.OrderByDescending(e => e.DeviceEventId).FirstOrDefault();
    }

    public IReadOnlyCollection<DeviceEvent> GetEventsFromLastCompletedSession()
    {
        var lastAppEnded = _context
            .DeviceEvents.Where(e => e.EventType == EventTypes.AppEnded)
            .OrderByDescending(e => e.DeviceEventId)
            .Select(e => (int?)e.DeviceEventId)
            .FirstOrDefault();

        if (!lastAppEnded.HasValue)
            return [];

        var lastAppStarted = _context
            .DeviceEvents.Where(e => e.EventType == EventTypes.AppStarted && e.DeviceEventId < lastAppEnded.Value)
            .OrderByDescending(e => e.DeviceEventId)
            .Select(e => (int?)e.DeviceEventId)
            .FirstOrDefault();

        if (!lastAppStarted.HasValue)
            return [];

        return _context
            .DeviceEvents.Where(e => e.DeviceEventId > lastAppStarted.Value && e.DeviceEventId < lastAppEnded.Value)
            .OrderBy(e => e.DeviceEventId)
            .ToList()
            .AsReadOnly();
    }

    public void SaveDeviceEvent(DeviceEvent deviceEvent)
    {
        try
        {
            _context.DeviceEvents.Add(deviceEvent);
            _context.SaveChanges();
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

    /// <summary>
    /// Recomputes total usage for a device from the event log.
    /// Used for snapshot rebuild/recovery.
    /// </summary>
    private TimeSpan ComputeTotalUsage(string deviceId)
    {
        var totalUsage = TimeSpan.Zero;
        DateTime? lastStartTime = null;
        var events = _context.DeviceEvents.Where(e => e.DeviceId == deviceId).OrderBy(e => e.DeviceEventId).ToList();

        foreach (var deviceEvent in events)
            if (deviceEvent.EventType.IsOpeningEvent())
            {
                lastStartTime = deviceEvent.Timestamp;
            }
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
        var lastAppEvent = _context
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

        // Backfill ConnectionEnded for devices that have more opening than closing events in the orphaned session.
        var orphanedSessionDeviceEvents = _context
            .DeviceEvents.Where(e => e.DeviceId != "" && e.Timestamp >= orphanedSessionStart)
            .ToList();

        var unbalancedDeviceIds = orphanedSessionDeviceEvents
            .GroupBy(e => e.DeviceId)
            .Where(group =>
                group.Count(e => e.EventType.IsOpeningEvent()) > group.Count(e => e.EventType.IsClosingEvent())
            )
            .Select(group => group.Key)
            .ToList();

        foreach (var deviceId in unbalancedDeviceIds)
            _context.DeviceEvents.Add(
                new DeviceEvent
                {
                    DeviceId = deviceId,
                    EventType = EventTypes.ConnectionEnded,
                    Timestamp = crashTime,
                }
            );

        _context.DeviceEvents.Add(new DeviceEvent { EventType = EventTypes.AppEnded, Timestamp = crashTime });

        _context.SaveChanges();
        Debug.WriteLine("RecoverFromCrash: wrote missing AppEnded and ConnectionEnded events.");
    }

    /// <summary>
    /// Rebuilds persisted device snapshots from the event log.
    /// DeviceEvents remain the repair source of truth; DeviceInfo acts as the fast-read snapshot.
    /// </summary>
    public void RebuildDeviceSnapshots()
    {
        try
        {
            var devices = _context.Devices.ToList();
            foreach (var device in devices)
            {
                device.TotalUsage = ComputeTotalUsage(device.DeviceId);
                device.SessionStartedAt = null;
            }

            _context.SaveChanges();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ERROR in RebuildDeviceSnapshots: {ex.Message}");
        }
    }
}
