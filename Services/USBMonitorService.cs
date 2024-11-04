using KeyPulse.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Windows;
using static KeyPulse.Models.USBDeviceInfo;

namespace KeyPulse.Services
{
    public class USBMonitorService : IDisposable
    {
        private ManagementEventWatcher? _insertWatcher;
        private ManagementEventWatcher? _removeWatcher;

        public ObservableCollection<USBDeviceInfo> ConnectedDevices => _connectedDevices;
        private readonly ObservableCollection<USBDeviceInfo> _connectedDevices;

        private readonly DataService _dataService;
        private readonly string _unknownDeviceName = "Unknown Device";
        private bool _disposed = false;

        public USBMonitorService() 
        {
            _connectedDevices = [];
            _dataService = new DataService();
            InitializeDevices();
            StartMonitoring();
        }

        private void InitializeDevices()
        {
            // get devices from the database
            var savedDevices = _dataService.GetAllDevices();
            // get currently connected devices
            var currentDevices = GetConnectedDevicesFromSystem();

            // add devices that are saved but not in the collection
            foreach (var device in savedDevices)
            {
                device.PropertyChanged += Device_PropertyChanged;
                if (currentDevices.Any(d => d.DeviceID == device.DeviceID))
                {
                    device.IsConnected = true;
                } else
                {
                    device.IsConnected = false;
                }
                _connectedDevices.Add(device);
            }

            // add devices that are currently connected but not in the database
            foreach (var device in currentDevices)
            {
                if (!savedDevices.Any(d => d.DeviceID == device.DeviceID))
                {
                    device.IsConnected = true;
                    device.PropertyChanged += Device_PropertyChanged;
                    _connectedDevices.Add(device);
                    _dataService.SaveDevice(device);
                }
            }
        }

        public void StartMonitoring()
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

        private void DeviceInsertedEvent(object sender, EventArrivedEventArgs e)
        {
            if (_disposed) return;

            try
            {
                ManagementBaseObject? instance = (ManagementBaseObject?)e.NewEvent["TargetInstance"];
                if (instance == null) return;

                USBDeviceInfo? deviceInfo = GetDeviceInfo(instance);
                if (deviceInfo != null)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var existingDevice = _connectedDevices.FirstOrDefault(d => d.DeviceID == deviceInfo.DeviceID);
                        if (existingDevice == null)
                        {
                            deviceInfo.IsConnected = true;
                            deviceInfo.PropertyChanged += Device_PropertyChanged;
                            _connectedDevices.Add(deviceInfo);
                            _dataService.SaveDevice(deviceInfo);
                        }
                        else
                        {
                            existingDevice.IsConnected = true;
                            _dataService.SaveDevice(existingDevice);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Exception in DeviceInsertedEvent: {ex.Message}");
            }
        }

        private void DeviceRemovedEvent(object sender, EventArrivedEventArgs e)
        {
            if (_disposed) return;

            try
            {
                ManagementBaseObject? instance = (ManagementBaseObject?)e.NewEvent["TargetInstance"];
                if (instance == null) return; 

                USBDeviceInfo? deviceInfo = GetDeviceInfo(instance);
                if (deviceInfo != null)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var existingDevice = _connectedDevices.FirstOrDefault(d => d.DeviceID == deviceInfo.DeviceID);
                        if (existingDevice != null)
                        {
                            existingDevice.IsConnected = false;
                            _dataService.SaveDevice(existingDevice);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Exception in DeviceRemovedEvent: {ex.Message}");
            }
        }

        private USBDeviceInfo? GetDeviceInfo(ManagementBaseObject obj) 
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

                return new USBDeviceInfo
                {
                    DeviceID = deviceId,
                    VID = vid,
                    PID = pid,
                    DeviceName = deviceName,
                    IsConnected = true,
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ManagementException in GetDeviceInfo: {ex.Message}");
                return null;
            }
        }

        private static string GetBaseDeviceId(string deviceId)
        {
            int miIndex = deviceId.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase);
            return miIndex >= 0 ? deviceId[..miIndex] : deviceId;
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

        public List<USBDeviceInfo> GetConnectedDevicesFromSystem()
        {
            List<USBDeviceInfo> connectedDevices = [];
            ManagementObjectSearcher searcher = new(@"
                SELECT * FROM Win32_PnPEntity 
                WHERE Service = 'kbdhid' OR Service = 'mouhid'
            ");
            try
            {
                foreach (ManagementBaseObject device in searcher.Get())
                {
                    USBDeviceInfo? deviceInfo = GetDeviceInfo(device);
                    if (deviceInfo != null)
                    {
                        var existingDevice = connectedDevices.Find(d => d.DeviceID == deviceInfo.DeviceID);
                        if (existingDevice == null)
                        {
                            connectedDevices.Add(deviceInfo);
                        } else 
                        {
                            existingDevice.DeviceName = _unknownDeviceName;
                        }
                    }
                }
            }
            catch (Exception ex) 
            {
                Debug.WriteLine($"Exception in GetConnectedDevicesFromSystem: {ex.Message}");
            }
                
             return connectedDevices;
        }
        private void Device_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is USBDeviceInfo device)
            {
                if (e.PropertyName == nameof(USBDeviceInfo.DeviceName))
                {
                    _dataService.SaveDevice(device);
                }
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        public void StopMonitoring()
        {
            Dispose();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
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
