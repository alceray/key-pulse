using System.Diagnostics;
using KeyPulse.Data;
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

    public DeviceInfo? GetDevice(string deviceId)
    {
        return _context.Devices.Find(deviceId);
    }

    public IReadOnlyCollection<DeviceInfo> GetAllDevices()
    {
        return _context.Devices.ToList().AsReadOnly();
    }

    public void SaveDevice(DeviceInfo device)
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

    public void AddDeviceEvent(DeviceEvent deviceEvent)
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
    public TimeSpan GetTotalUsage(string deviceId)
    {
        var totalUsage = TimeSpan.Zero;
        DateTime? lastStartTime = null;
        var events = _context.DeviceEvents.Where(e => e.DeviceId == deviceId).OrderBy(e => e.DeviceEventId).ToList();

        foreach (var deviceEvent in events)
            if (deviceEvent.EventType.IsOpening())
            {
                lastStartTime = deviceEvent.Timestamp;
            }
            else if (deviceEvent.EventType.IsClosing() && lastStartTime.HasValue)
            {
                totalUsage += deviceEvent.Timestamp - lastStartTime.Value;
                lastStartTime = null;
            }

        // If currently connected, add the time since the last opening event
        if (lastStartTime.HasValue)
            totalUsage += DateTime.Now - lastStartTime.Value;

        return totalUsage;
    }

    /// <summary>
    /// Gets the last time a device was connected.
    /// Checks both runtime Connected events and startup ConnectionStarted events
    /// (where the device was already plugged in before the app started),
    /// and returns the more recent of the two.
    /// </summary>
    public DateTime? GetLastConnectedTime(string deviceId)
    {
        var lastConnected = _context
            .DeviceEvents.Where(e => e.DeviceId == deviceId && e.EventType == EventTypes.Connected)
            .OrderByDescending(e => e.DeviceEventId)
            .Select(e => (DateTime?)e.Timestamp)
            .FirstOrDefault();

        // Find the most recent ConnectionStarted where the device had no ConnectionEnded
        // in the previous session — meaning it was already plugged in before that startup.
        var lastConnectionStarted = (
            from cs in _context.DeviceEvents
            where cs.DeviceId == deviceId && cs.EventType == EventTypes.ConnectionStarted

            // This session's AppStarted ID
            let sessionStartId = _context
                .DeviceEvents.Where(a => a.EventType == EventTypes.AppStarted && a.DeviceEventId <= cs.DeviceEventId)
                .Max(a => (int?)a.DeviceEventId)
            where sessionStartId != null

            // Previous session's AppEnded ID
            let prevEndId = _context
                .DeviceEvents.Where(a => a.EventType == EventTypes.AppEnded && a.DeviceEventId < sessionStartId)
                .Max(a => (int?)a.DeviceEventId)
            where prevEndId != null

            // Previous session's AppStarted ID (null = no earlier session, check from beginning)
            let prevStartId = _context
                .DeviceEvents.Where(a => a.EventType == EventTypes.AppStarted && a.DeviceEventId < prevEndId)
                .Max(a => (int?)a.DeviceEventId)

            // Qualifies only if device connection was not ended in the previous session
            where
                !_context.DeviceEvents.Any(d =>
                    d.DeviceId == deviceId
                    && d.EventType == EventTypes.ConnectionEnded
                    && d.DeviceEventId <= prevEndId
                    && (prevStartId == null || d.DeviceEventId >= prevStartId)
                )
            orderby cs.DeviceEventId descending
            select (DateTime?)cs.Timestamp
        ).FirstOrDefault();

        // Get earliest ConnectionStarted if not already set
        lastConnectionStarted ??= _context
            .DeviceEvents.Where(e => e.DeviceId == deviceId && e.EventType == EventTypes.ConnectionStarted)
            .Min(e => (DateTime?)e.Timestamp);

        return lastConnected > lastConnectionStarted ? lastConnected : lastConnectionStarted;
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

        // If the last app-lifecycle event was an AppStarted with no AppEnded, the previous
        // session didn't shut down cleanly — fill in the missing closing events.
        if (lastAppEvent == null || lastAppEvent.EventType != EventTypes.AppStarted)
            return;

        var orphanedSessionStart = lastAppEvent.Timestamp;

        // Write ConnectionEnded for any devices that were still active in that session
        var orphanedActiveDevices = _context
            .DeviceEvents.Where(e =>
                e.DeviceId != ""
                && (e.EventType == EventTypes.ConnectionStarted || e.EventType == EventTypes.Connected)
                && e.Timestamp >= orphanedSessionStart
            )
            .Select(e => e.DeviceId)
            .Distinct()
            .ToList();

        foreach (var deviceId in orphanedActiveDevices)
        {
            // Only add ConnectionEnded if it wasn't already closed in that session
            var wasClosedInSession = _context.DeviceEvents.Any(e =>
                e.DeviceId == deviceId
                && (
                    e.EventType == EventTypes.ConnectionEnded
                    || e.EventType == EventTypes.Disconnected
                    || e.EventType == EventTypes.Suspended
                )
                && e.Timestamp >= orphanedSessionStart
            );

            if (!wasClosedInSession)
                _context.DeviceEvents.Add(
                    new DeviceEvent
                    {
                        DeviceId = deviceId,
                        EventType = EventTypes.ConnectionEnded,
                        Timestamp = orphanedSessionStart, // best-effort timestamp
                    }
                );
        }

        // Write the missing AppEnded
        _context.DeviceEvents.Add(
            new DeviceEvent
            {
                EventType = EventTypes.AppEnded,
                Timestamp = orphanedSessionStart, // best-effort timestamp
            }
        );

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
                device.TotalUsage = GetTotalUsage(device.DeviceId);
                device.LastConnectedAt = GetLastConnectedTime(device.DeviceId);
                device.IsActive = false;
            }

            _context.SaveChanges();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ERROR in RebuildDeviceSnapshots: {ex.Message}");
        }
    }
}
