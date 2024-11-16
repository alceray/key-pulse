using KeyPulse.Data;
using KeyPulse.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Windows;
using static KeyPulse.Models.DeviceInfo;

namespace KeyPulse.Services
{
    public class USBMonitorService : IDisposable
    {
        private ManagementEventWatcher? _insertWatcher;
        private ManagementEventWatcher? _removeWatcher;

        public ObservableCollection<DeviceInfo> AllDevices;
        public ObservableCollection<Connection> ConnectionEvents;
       
        private readonly DataService _dataService;
        private readonly string _unknownDeviceName;
        private bool _disposed;

        public USBMonitorService(DataService dataService) 
        {
            _dataService = dataService;
            AllDevices = new(_dataService.GetAllDevices());
            ConnectionEvents = new(_dataService.GetAllConnections());
            _unknownDeviceName = "Unknown Device";
            _disposed = false;

            SetCurrentDevicesFromSystem();
            StartMonitoring();

            if (_dataService.ActiveConnectionExists())
            {
                Debug.WriteLine("Initialized with still active connection");
            }
        }
                
        private void AddOrUpdateDevice(DeviceInfo device)
        {
            var existingDevice = AllDevices.FirstOrDefault(d => d.DeviceID == device.DeviceID);
            if (existingDevice == null)
            {
                device.PropertyChanged += Device_PropertyChanged;
                AllDevices.Add(device);
                _dataService.SaveDevice(device);
            }
            else if (existingDevice.DeviceName != device.DeviceName || existingDevice.DeviceType != device.DeviceType)
            {
                existingDevice.DeviceName = device.DeviceName;
                existingDevice.DeviceType = device.DeviceType;
                _dataService.SaveDevice(device);
            }
        }

        private void UpdateOrAddConnection(Connection connection)
        {
            var existingConnection = ConnectionEvents.FirstOrDefault(c => c.ConnectionID == connection.ConnectionID);
            if (existingConnection == null)
            {
                ConnectionEvents.Add(connection);
            }
            else
            {
                existingConnection.DisconnectedAt = connection.DisconnectedAt;
            }
            _dataService.SaveConnection(connection);
        }

        private void DeviceInsertedEvent(object sender, EventArrivedEventArgs e)
        {
            ManagementBaseObject? instance = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            if (instance == null) return;

            DeviceInfo? device = GetDeviceInfo(instance);
            if (device == null) return;

            AddOrUpdateDevice(device);
            CreateNewConnectionIfNeeded(device);
        }

        private void DeviceRemovedEvent(object sender, EventArrivedEventArgs e)
        {
            ManagementBaseObject? instance = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            if (instance == null) return; 

            DeviceInfo? device = GetDeviceInfo(instance);
            if (device == null) return;
                
            if (_dataService.DeviceExists(device.DeviceID))
            {
                var activeConnections = _dataService.GetAllConnections(device.DeviceID, onlyActive: true);
                if (activeConnections.Count == 1)
                {
                    Connection activeConnection = activeConnections.First();
                    activeConnection.DisconnectedAt = DateTime.Now;
                    UpdateOrAddConnection(activeConnection);
                }
                else if (activeConnections.Count > 1)
                {
                    Debug.WriteLine($"There are multiple active connections with DeviceID {device.DeviceID}");
                }
                else
                {
                    Debug.WriteLine($"There are no active connection with DeviceID {device.DeviceID}");
                }
            }
            else
            {
                throw new Exception("Device removed was not saved");
            }
        }

        private DeviceInfo? GetDeviceInfo(ManagementBaseObject obj) 
        {
            if (obj == null) return null;

            try
            {
                string? hidDeviceId = obj.GetPropertyValue("DeviceID")?.ToString();
                if (string.IsNullOrEmpty(hidDeviceId)) return null;

                string? vid = GetValueFromDeviceId(hidDeviceId, "VID_");
                string? pid = GetValueFromDeviceId(hidDeviceId, "PID_");
                if (string.IsNullOrEmpty(vid) || string.IsNullOrEmpty(pid)) return null;

                string deviceName = obj.GetPropertyValue("Name")?.ToString() ?? _unknownDeviceName;
                string deviceId = $"USB\\VID_{vid}&PID_{pid}";

                //Console.WriteLine($"Properties of {deviceName}:");
                //foreach (var property in obj.Properties)
                //{
                //    string name = property.Name;
                //    object value = property.Value;
                //    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(value?.ToString()))
                //    {
                //        Console.WriteLine($"{name}: {value}");
                //    }
                //}

                return new DeviceInfo
                {
                    DeviceID = deviceId,
                    VID = vid,
                    PID = pid,
                    DeviceName = deviceName,
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ManagementException in GetDeviceInfo: {ex.Message}");
                return null;
            }
        }

        private void CreateNewConnectionIfNeeded(DeviceInfo device)
        {
            if (!_dataService.ActiveConnectionExists(device.DeviceID))
            {
                Connection newConnection = new() { DeviceID = device.DeviceID };
                UpdateOrAddConnection(newConnection);
            }
        }

        private static string GetValueFromDeviceId(string? deviceId, string identifier)
        {
            if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(identifier))
                return "";

            int startIndex = deviceId.IndexOf(identifier, StringComparison.OrdinalIgnoreCase);
            if (startIndex < 0)
                return "";
            startIndex += identifier.Length;

            int endIndex = deviceId.IndexOfAny(['&' ,'\\'], startIndex);
            if (endIndex < 0)
                endIndex = deviceId.Length;

            return deviceId[startIndex..endIndex];
        }

        private void Device_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is DeviceInfo device)
            {
                if (e.PropertyName == nameof(DeviceInfo.DeviceName))
                {
                    AddOrUpdateDevice(device);
                }
            }
        }

        public void StartMonitoring()
        {
            if (_disposed) return;

            try
            {
                WqlEventQuery insertQuery = new(@"
                    SELECT * FROM __InstanceCreationEvent WITHIN 2 
                    WHERE TargetInstance ISA 'Win32_PnPEntity' 
                    AND (TargetInstance.Service = 'kbdhid' OR TargetInstance.Service = 'mouhid')
                ");
                _insertWatcher = new ManagementEventWatcher(insertQuery);
                _insertWatcher.EventArrived += DeviceInsertedEvent;
                _insertWatcher.Start();

                WqlEventQuery removeQuery = new(@"
                    SELECT * FROM __InstanceDeletionEvent WITHIN 2 
                    WHERE TargetInstance ISA 'Win32_PnPEntity' 
                    AND (TargetInstance.Service = 'kbdhid' OR TargetInstance.Service = 'mouhid')
                ");
                _removeWatcher = new ManagementEventWatcher(removeQuery);
                _removeWatcher.EventArrived += DeviceRemovedEvent;
                _removeWatcher.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Exception in StartMonitoring: {ex.Message}");
            }
        }

        public void SetCurrentDevicesFromSystem()
        {
            try
            {
                ManagementObjectSearcher searcher = new(@"
                    SELECT * FROM Win32_PnPEntity 
                    WHERE Service = 'kbdhid' OR Service = 'mouhid'
                ");
                foreach (ManagementBaseObject obj in searcher.Get())
                {
                    DeviceInfo? device = GetDeviceInfo(obj);
                    if (device != null)
                    {
                        AddOrUpdateDevice(device);
                        CreateNewConnectionIfNeeded(device);
                    }
                }
            }
            catch (Exception ex) 
            {
                Debug.WriteLine($"Exception in SetCurrentDevicesFromSystem: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                foreach (var connection in _dataService.GetAllConnections(onlyActive: true))
                {
                    connection.DisconnectedAt = DateTime.Now;
                    _dataService.SaveConnection(connection);
                }

                foreach (var device in AllDevices)
                    device.PropertyChanged -= Device_PropertyChanged;

                if (_insertWatcher != null)
                {
                    _insertWatcher.EventArrived -= DeviceInsertedEvent;
                    _insertWatcher.Stop();
                    _insertWatcher.Dispose();
                    _insertWatcher = null;
                }
                if (_removeWatcher != null)
                {
                    _removeWatcher.EventArrived -= DeviceRemovedEvent;
                    _removeWatcher.Stop();
                    _removeWatcher.Dispose();
                    _removeWatcher = null;
                }
            }

            _disposed = true;
        }

        ~USBMonitorService() 
        {
            Dispose(disposing: false);
        }
    }
}
