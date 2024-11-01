using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KeyPulse.Models;
using System.Windows;
using KeyPulse.Data;

namespace KeyPulse.Services
{
    public class DataService
    {
        private readonly string _databasePath;

        public DataService()
        {
            _databasePath = GetDatabasePath();
            InitializeDatabase();
        }

        public static string GetDatabasePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appFolder = Path.Combine(appData, Application.Current?.MainWindow?.Title ?? "KeyPulse");
            if (!Directory.Exists(appFolder))
            {
                Directory.CreateDirectory(appFolder);
            }
            return Path.Combine(appFolder, "devices.db");
        }

        private void InitializeDatabase()
        {
            if (File.Exists(_databasePath)) return;
            using var context = new ApplicationDbContext();
            context.Database.EnsureCreated();
        }

        public List<USBDeviceInfo> GetAllDevices()
        {
            using var context = new ApplicationDbContext();
            return context.Devices.ToList();
        }

        public void SaveDevice(USBDeviceInfo device)
        {
            using var context = new ApplicationDbContext();
            var existingDevice = context.Devices.Find(device.DeviceID);
            if (existingDevice == null)
            {
                context.Devices.Add(device);
            }
            else
            {
                context.Entry(existingDevice).CurrentValues.SetValues(device);
            }
            context.SaveChanges();
        }

        public void DeleteDevice(string deviceId)
        {
            using var context = new ApplicationDbContext();
            var device = context.Devices.Find(deviceId);
            if (device != null)
            {
                context.Devices.Remove(device);
                context.SaveChanges();
            }
        }
    }
}
