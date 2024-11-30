using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KeyPulse.Models;
using System.Windows;
using KeyPulse.Data;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Diagnostics;

namespace KeyPulse.Services
{
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
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appName = Assembly.GetExecutingAssembly().GetName().Name ?? "KeyPulse";
            string appFolder = Path.Combine(appData, appName);
            if (!Directory.Exists(appFolder))
            {
                Directory.CreateDirectory(appFolder);
            }
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

        public IReadOnlyCollection<DeviceInfo> GetAllDevices(bool activeOnly = false)
        {
            return activeOnly
                ? _context.Devices.Where(d => d.IsActive).ToList().AsReadOnly()
                : _context.Devices.ToList().AsReadOnly();
        }

        public void SaveDevice(DeviceInfo device)
        {
            try
            {
                var existingDevice = _context.Devices.SingleOrDefault(d => d.DeviceId == device.DeviceId);
                if (existingDevice != null)
                {
                    _context.Entry(existingDevice).CurrentValues.SetValues(device);
                }
                else
                {
                    _context.Devices.Add(device);
                }
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
            try {
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
    }
}
