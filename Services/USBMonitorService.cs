using KeyPulse.Models;
using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;

namespace KeyPulse.Services
{
    public class USBMonitorService : IDisposable
    {
        public event EventHandler<USBDeviceInfo>? DeviceInserted;
        public event EventHandler<USBDeviceInfo>? DeviceRemoved;

        private ManagementEventWatcher? _insertWatcher;
        private ManagementEventWatcher? _removeWatcher;

        private readonly List<USBDeviceInfo> _connectedDevices;
        private readonly string _unknownDeviceName = "Unknown Device";
        private bool _disposed = false;

        public USBMonitorService() 
        {
            _connectedDevices = GetConnectedDevicesFromSystem();
            StartMonitoring();
        }

        public void StartMonitoring()
        {
            WqlEventQuery insertQuery = new WqlEventQuery("SELECT * FROM __InstanceCreationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_USBControllerDevice'");
            _insertWatcher = new ManagementEventWatcher(insertQuery);
            _insertWatcher.EventArrived += DeviceInsertedEvent;
            _insertWatcher.Start();

            WqlEventQuery removeQuery = new WqlEventQuery("SELECT * FROM __InstanceDeletionEVent WITHIN 2 WHERE TargetInstance ISA 'Win32_USBControllerDevice'");
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
                string? dependent = instance?["Dependent"]?.ToString();
                if (string.IsNullOrEmpty(dependent)) return;

                ManagementPath dependentPath = new(dependent);
                using ManagementObject device = new(dependentPath);

                USBDeviceInfo? deviceInfo = GetDeviceInfo(device);
                if (deviceInfo != null)
                {
                    _connectedDevices.Add(deviceInfo);
                    DeviceInserted?.Invoke(this, deviceInfo);
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
                string? dependent = instance?["Dependent"]?.ToString();
                if (string.IsNullOrEmpty(dependent)) return;

                ManagementPath dependentPath = new(dependent);
                using ManagementObject device = new(dependentPath);

                USBDeviceInfo? deviceInfo = GetDeviceInfo(device);
                if (deviceInfo != null)
                {
                    var deviceToRemove = _connectedDevices.FirstOrDefault(d => d.DeviceID == deviceInfo.DeviceID);
                    if (deviceToRemove != null)
                    {
                        _connectedDevices.Remove(deviceInfo);
                    }
                    DeviceRemoved?.Invoke(this, deviceInfo);
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
                string? deviceId = obj["DeviceID"]?.ToString();
                string deviceName = obj["Name"]?.ToString() ?? _unknownDeviceName;
                string? vid = GetValueFromDeviceId(deviceId, "VID_");
                string? pid = GetValueFromDeviceId(deviceId, "PID_");
                if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(vid) || string.IsNullOrEmpty(pid)) return null;
                deviceId = GetBaseDeviceId(deviceId);

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
                    DeviceName = deviceName
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

        public List<USBDeviceInfo> GetConnectedDevices()
        {
            return new List<USBDeviceInfo>(_connectedDevices);
        }

        public List<USBDeviceInfo> GetConnectedDevicesFromSystem()
        {
            List<USBDeviceInfo> connectedDevices = [];
            ManagementObjectSearcher searcher = new(@"Select * from Win32_PnPEntity where Service = 'kbdhid' or Service = 'mouhid'");
            try
            {
                foreach (ManagementBaseObject device in searcher.Get())
                {
                    USBDeviceInfo? deviceInfo = GetDeviceInfo(device);
                    if (deviceInfo != null)
                    {
                        var deviceInList = connectedDevices.Find(d => d.DeviceID == deviceInfo.DeviceID);
                        if (deviceInList == null)
                        {
                            connectedDevices.Add(deviceInfo);
                        } 
                        else if (deviceInList.DeviceName != _unknownDeviceName)
                        {
                            deviceInList.DeviceName = _unknownDeviceName;
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
