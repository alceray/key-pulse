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

        public IReadOnlyCollection<DeviceInfo> GetAllDevices()
        {
            return _context.Devices.ToList().AsReadOnly();
        }

        public void SaveDevice(DeviceInfo device)
        {
            try
            {
                _context.Update(device);
                _context.SaveChanges();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Exception in SaveDevice: {ex.Message}");
            }
        }

        public bool DeviceExists(string deviceId)
        {
            return _context.Devices.Find(deviceId) != null;
        }

        public IReadOnlyCollection<Connection> GetAllConnections(string? deviceId = null, bool onlyActive = false)
        {
            var query = _context.Connections.AsQueryable();

            if (onlyActive)
            {
                query = query.Where(c => c.DisconnectedAt == null);
            }
            if (!string.IsNullOrEmpty(deviceId))
            {
                query = query.Where(c => c.DeviceID == deviceId);
            }

            return query.ToList().AsReadOnly();
        }

        public void SaveConnection(Connection connection)
        {
            try { 
                _context.Update(connection);
                _context.SaveChanges();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Exception in SaveConnection: {ex.Message}");
            }
        }

        public bool ActiveConnectionExists(string? deviceId = null)
        {
            return deviceId == null
                ? _context.Connections.Any(c => c.DisconnectedAt == null)
                : _context.Connections.Any(c => c.DeviceID == deviceId && c.DisconnectedAt == null);
        }
    }
}
