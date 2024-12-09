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

        public ObservableCollection<DeviceInfo> DeviceList;
        public ObservableCollection<DeviceEvent> DeviceEventList;
       
        private readonly DataService _dataService;
        private readonly HashSet<string> _recentlyProcessedDevices = [];
        private readonly string _unknownDeviceName = "Unknown Device";
        private bool _disposed = false;

        public USBMonitorService(DataService dataService) 
        {
            _dataService = dataService;
            DeviceList = new(_dataService.GetAllDevices());
            DeviceEventList = new(_dataService.GetAllDeviceEvents());

            if (_dataService.IsAnyDeviceActive())
            {
                throw new InvalidOperationException("Cannot initialize usb monitor service with active devices");
            }

            SetCurrentDevicesFromSystem();
            StartMonitoring();
        }
                
        private void UpsertDevice(DeviceInfo device, bool isActive)
        {
            if (!DeviceList.Any(d => d.DeviceId == device.DeviceId))
            {
                device.PropertyChanged += Device_PropertyChanged;
                Application.Current.Dispatcher.BeginInvoke(() => DeviceList.Add(device));
            }
            device.IsActive = isActive;
            _dataService.SaveDevice(device);
        }

        private void AddDeviceEvent(DeviceEvent deviceEvent)
        {
            Application.Current.Dispatcher.BeginInvoke(() => DeviceEventList.Add(deviceEvent));
            _dataService.AddDeviceEvent(deviceEvent);
        }

        private void DeviceInsertedEvent(object sender, EventArrivedEventArgs e)
        {
            try
            {
                var now = DateTime.Now;

                ManagementBaseObject? instance = (ManagementBaseObject)e.NewEvent["TargetInstance"];
                if (instance == null) 
                    return;

                string? deviceId = ExtractDeviceId(instance);
                if (string.IsNullOrEmpty(deviceId)) 
                    return;

                // for every actual connection, 3 insert events are triggered within a very short timeframe, so this cache of
                // recently processed devices helps to prevent inserting duplicate events in the db. this is merely a workaround
                // and more work will be necessary for a foolproof solution. the delay was arbitrarily set to 0.2 seconds.
                if (_recentlyProcessedDevices.Contains(deviceId)) 
                    return;
                _recentlyProcessedDevices.Add(deviceId);
                Task.Delay(200).ContinueWith(_ => _recentlyProcessedDevices.Remove(deviceId));

                DeviceInfo? device = _dataService.GetDevice(deviceId);
                if (device == null) 
                    return;

                if (!device.IsActive)
                {
                    AddDeviceEvent(new()
                    {
                        Timestamp = now,
                        DeviceId = deviceId,
                        EventType = EventTypes.Connected
                    });
                    UpsertDevice(device, isActive: true);
                }
                else
                {
                    throw new Exception($"Device with DeviceID {device.DeviceId} was already active");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ERROR in DeviceInsertedEvent: {ex.Message}");
            }
        }

        private void DeviceRemovedEvent(object sender, EventArrivedEventArgs e)
        {
            try {
                ManagementBaseObject? instance = (ManagementBaseObject)e.NewEvent["TargetInstance"];
                if (instance == null) 
                    return;

                string? deviceId = ExtractDeviceId(instance);
                if (string.IsNullOrEmpty(deviceId))
                    return;

                DeviceInfo? device = _dataService.GetDevice(deviceId);
                if (device == null)
                    return;

                if (device.IsActive)
                {
                    AddDeviceEvent(new()
                    {
                        DeviceId = device.DeviceId,
                        EventType = EventTypes.Disconnected
                    });
                    UpsertDevice(device, isActive: false);
                }
                else
                {
                    throw new Exception($"Device with DeviceID {device.DeviceId} was already inactive");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ERROR in DeviceRemovedEvent: {ex.Message}");
            }
        }

        private string? ExtractDeviceId(ManagementBaseObject? obj)
        {
            if (obj == null) 
                return null;

            string? hidDeviceId = obj.GetPropertyValue("DeviceID")?.ToString();
            if (string.IsNullOrEmpty(hidDeviceId))
                return null;

            string? vid = GetValueFromDeviceId(hidDeviceId, "VID_");
            string? pid = GetValueFromDeviceId(hidDeviceId, "PID_");
            if (string.IsNullOrEmpty(vid) || string.IsNullOrEmpty(pid))
                return null;

            return $"USB\\VID_{vid}&PID_{pid}";
        }

        private DeviceInfo? ExtractDeviceInfo(ManagementBaseObject? obj) 
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
                    DeviceId = deviceId,
                    VID = vid,
                    PID = pid,
                    DeviceName = deviceName,
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ERROR in GetDeviceInfo: {ex.Message}");
                return null;
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
                    _dataService.SaveDevice(device);
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
                Debug.WriteLine($"ERROR in StartMonitoring: {ex.Message}");
            }
        }

        public void SetCurrentDevicesFromSystem()
        {
            try
            {
                AddDeviceEvent(new() { EventType = EventTypes.AppStarted });

                ManagementObjectSearcher searcher = new(@"
                    SELECT * FROM Win32_PnPEntity 
                    WHERE Service = 'kbdhid' OR Service = 'mouhid'
                ");

                foreach (ManagementBaseObject obj in searcher.Get())
                {
                    DeviceInfo? currDevice = ExtractDeviceInfo(obj);
                    if (currDevice == null) 
                        continue;

                    if (_recentlyProcessedDevices.Contains(currDevice.DeviceId))
                        continue;
                    _recentlyProcessedDevices.Add(currDevice.DeviceId);

                    AddDeviceEvent(new()
                    {
                        DeviceId = currDevice.DeviceId,
                        EventType = EventTypes.ConnectionStarted
                    });
                    UpsertDevice(currDevice, isActive: true);
                }
                
                _recentlyProcessedDevices.Clear();
            }
            catch (Exception ex) 
            {
                Debug.WriteLine($"ERROR in SetCurrentDevicesFromSystem: {ex.Message}");
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
                foreach (var device in _dataService.GetAllDevices(activeOnly: true))
                {
                    AddDeviceEvent(new()
                    {
                        DeviceId = device.DeviceId,
                        EventType = EventTypes.ConnectionEnded
                    });
                    UpsertDevice(device, isActive: false);
                }

                AddDeviceEvent(new() { EventType = EventTypes.AppEnded });

                foreach (var device in DeviceList)
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
