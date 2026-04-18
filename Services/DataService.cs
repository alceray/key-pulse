using System.Diagnostics;
using System.IO;
using System.Reflection;
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

    public static string GetDatabasePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appName = Assembly.GetExecutingAssembly().GetName().Name ?? "KeyPulse";
        var appFolder = Path.Combine(appData, appName);
        if (!Directory.Exists(appFolder))
            Directory.CreateDirectory(appFolder);
        return Path.Combine(appFolder, "devices.db");
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
            var existingDevice = _context.Devices.SingleOrDefault(d =>
                d.DeviceId == device.DeviceId
            );
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

    public TimeSpan GetTotalUsage(string deviceId)
    {
        var totalUsage = TimeSpan.Zero;
        DateTime? lastStartTime = null;
        var events = _context
            .DeviceEvents.Where(e => e.DeviceId == deviceId)
            .OrderBy(e => e.Timestamp)
            .ToList();

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

        return totalUsage;
    }
}
