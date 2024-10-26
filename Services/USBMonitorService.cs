using KeyPulse.Models;
using System.Diagnostics;
using System.Management;

namespace KeyPulse.Services
{
    public class USBMonitorService : IDisposable
    {
        public event EventHandler<USBDeviceInfo>? DeviceInserted;
        public event EventHandler<USBDeviceInfo>? DeviceRemoved;
        private ManagementEventWatcher? _insertWatcher;
        private ManagementEventWatcher? _removeWatcher;
        private readonly List<USBDeviceInfo> _connectedDevices;
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
                //Console.WriteLine("Properties of ManagementBaseObject:");
                //foreach (var property in obj.Properties)
                //{
                //    string name = property.Name;
                //    object value = property.Value;
                //    Console.WriteLine($"Name: {name}, Value: {value}");
                //}

                string? deviceId = obj["DeviceID"]?.ToString();
                string? pnpDeviceId = obj["PNPDeviceID"]?.ToString();
                string deviceName = obj["Name"]?.ToString() ?? "Unknown Device";
                string? vid = GetValueFromDeviceId(pnpDeviceId, "VID_");
                string? pid = GetValueFromDeviceId(pnpDeviceId, "PID_");
                
                if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(pnpDeviceId) || string.IsNullOrEmpty(vid) || string.IsNullOrEmpty(pid)) return null;
                if (!IsUsbDevice(pnpDeviceId)) return null;

                return new USBDeviceInfo
                {
                    DeviceID = deviceId,
                    PnpDeviceID = pnpDeviceId,
                    VID = vid,
                    PID = pid,
                    DeviceName = deviceName
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ManagementException in GetDeviceInfo: {ex.Message}");
                return null;
            }
        }

        private static bool IsUsbDevice(string? pnpDeviceId)
        {
            if (string.IsNullOrEmpty(pnpDeviceId)) return false;
            return pnpDeviceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase);
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

        public void StopMonitoring()
        {
            Dispose();
        }

        public List<USBDeviceInfo> GetConnectedDevices()
        {
            // Return a copyo to prevent external modification
            return new List<USBDeviceInfo>(_connectedDevices);
        }

        public List<USBDeviceInfo> GetConnectedDevicesFromSystem()
        {
            List<USBDeviceInfo> connectedDevices = [];

            // Retrieve all associations between USB controllers and connected devices
            ManagementObjectSearcher searcher = new(@"Select * From Win32_USBControllerDevice");
            try
            {
                foreach (ManagementBaseObject association in searcher.Get())
                {
                    string? dependent = association["Dependent"]?.ToString();
                    if (string.IsNullOrEmpty(dependent)) continue;

                    ManagementPath dependentPath = new(dependent);
                    using ManagementObject device = new(dependentPath);

                    USBDeviceInfo? deviceInfo = GetDeviceInfo(device);
                    if (deviceInfo != null)
                    {
                        connectedDevices.Add(deviceInfo);
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
