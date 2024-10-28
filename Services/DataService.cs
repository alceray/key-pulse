using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Data.SQLite;
using KeyPulse.Models;
using System.Windows;

namespace KeyPulse.Services
{
    public class DataService
    {
        private readonly string _databasePath;
        private readonly string _connectionString;

        public DataService()
        {
            _databasePath = GetDatabasePath();
            _connectionString = $"Data Source={_databasePath};Version=3;";
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

            SQLiteConnection.CreateFile(_databasePath);

            using var connection = new SQLiteConnection(_connectionString);
            connection.Open();
            string createTableQuery = @"
                CREATE TABLE Devices (
                    DeviceID TEXT PRIMARY KEY NOT NULL,
                    VID TEXT NOT NULL,
                    PID TEXT NOT NULL,
                    DeviceName TEXT NOT NULL
                );";
            using var command = new SQLiteCommand(createTableQuery, connection);
            command.ExecuteNonQuery();
        }

        public List<USBDeviceInfo> GetAllDevices()
        {
            List<USBDeviceInfo> devices = [];
            using SQLiteConnection connection = new(_connectionString);
            connection.Open();
            string query = "SELECT * FROM Devices";
            using var command = new SQLiteCommand(query, connection);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var device = new USBDeviceInfo
                {
                    DeviceID = reader.GetString(reader.GetOrdinal("DeviceID")),
                    VID = reader.GetString(reader.GetOrdinal("VID")),
                    PID = reader.GetString(reader.GetOrdinal("PID")),
                    DeviceName = reader.GetString(reader.GetOrdinal("DeviceName"))
                };
                devices.Add(device);
            }

            return devices;
        }

        public void SaveDevice(USBDeviceInfo device)
        {
            using var connection = new SQLiteConnection(_connectionString);
            connection.Open();
            string insertOrReplaceQuery = @"INSERT OR REPLACE INTO Devices (DeviceID, VID, PID, DeviceName) VALUES (@DeviceID, @VID, @PID, @DeviceName);";
            using var command = new SQLiteCommand(insertOrReplaceQuery, connection);
            command.Parameters.AddWithValue("@DeviceID", device.DeviceID);
            command.Parameters.AddWithValue("@VID", device.VID);
            command.Parameters.AddWithValue("@PID", device.PID);
            command.Parameters.AddWithValue("@DeviceName", device.DeviceName);
            command.ExecuteNonQuery();
        }

        public void DeleteDevice(string deviceId)
        {
            using var connection = new SQLiteConnection(_connectionString);
            connection.Open();
            string deleteQuery = "DELETE FROM Devices WHERE DeviceID = @DeviceID";
            using var command = new SQLiteCommand(deleteQuery, connection);
            command.Parameters.AddWithValue("@DeviceID", deviceId);
            command.ExecuteNonQuery();
        }
    }
}
