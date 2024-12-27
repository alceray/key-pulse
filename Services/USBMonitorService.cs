using KeyPulse.Data;
using KeyPulse.Helpers;
using KeyPulse.Models;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
        // for every irl connection, 2-3 insert events are created within a very short timeframe, so this cache of
        // recently inserted devices helps to prevent inserting duplicate connected events in the db.
        private readonly ConcurrentDictionary<string, (int KeyboardCount, int MouseCount, DateTime FirstTimestamp)> _recentlyInsertedDevices = new();
        private readonly string _unknownDeviceName = "Unknown Device";
        private bool _disposed = false;

        public USBMonitorService(DataService dataService) 
        {
            _dataService = dataService;
            DeviceList = GetAllDevices();
            DeviceEventList = new(_dataService.GetAllDeviceEvents());

            // TODO: Handle app/system crashes before uncommenting
            //if (_dataService.IsAnyDeviceActive())
            //{
            //    throw new InvalidOperationException("Cannot initialize usb monitor service with active devices");
            //}

            SetCurrentDevicesFromSystem();
            StartMonitoring();
        }
                
        private void printObj(ManagementBaseObject obj)
        {
            if (obj == null)
                return;

            Debug.WriteLine("OBJECT PROPERTIES");
            foreach (var property in obj.Properties)
            {
                string name = property.Name;
                object value = property.Value;
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(value?.ToString()))
                {
                    Debug.WriteLine($"{name}: {value}");
                }
            }
        }

        private ObservableCollection<DeviceInfo> GetAllDevices()
        {
            var devices = _dataService.GetAllDevices();
            foreach (var device in devices)
            {
                device.PropertyChanged += Device_PropertyChanged;
            }
            return new(devices);
        }

        private void UpsertDevice(DeviceInfo device, bool isActive)
        {
            if (!DeviceList.Any(d => d.DeviceId == device.DeviceId))
            {
                device.PropertyChanged += Device_PropertyChanged;
                Application.Current.Dispatcher.Invoke(() => DeviceList.Add(device));
            }
            device.IsActive = isActive;
            if (!isActive)
                device.TotalUsage = _dataService.GetTotalUsage(device.DeviceId);
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
                ManagementBaseObject? instance = (ManagementBaseObject)e.NewEvent["TargetInstance"];
                if (instance == null) 
                    return;

                //printObj(instance);

                string? deviceId = ExtractDeviceId(instance);
                if (string.IsNullOrEmpty(deviceId)) 
                    return;

                int keyboardIncrement = instance.GetPropertyValue("PNPClass")?.ToString() == "Keyboard" ? 1 : 0;
                int mouseIncrement = instance.GetPropertyValue("PNPClass")?.ToString() == "Mouse" ? 1 : 0;

                if (_recentlyInsertedDevices.TryGetValue(deviceId, out var value))
                {
                    var (keyboardCount, mouseCount, firstTimestamp) = value;
                    keyboardCount += keyboardIncrement;
                    mouseCount += mouseIncrement;
                    _recentlyInsertedDevices[deviceId] = (keyboardCount, mouseCount, firstTimestamp);

                    // wait until we have at least 2 events to determine the device type and save the device to db
                    if (keyboardCount + mouseCount < 2)
                        return;

                    //Debug.WriteLine($"Inserted device {deviceId}: {keyboardCount} {mouseCount}");

                    var deviceType = keyboardCount > mouseCount ? DeviceTypes.Keyboard : DeviceTypes.Mouse;
                    DeviceInfo? device = _dataService.GetDevice(deviceId);
                    if (device == null)
                    {
                        var deviceName = PowershellScripts.GetDeviceName(deviceId) ?? _unknownDeviceName;
                        device = new DeviceInfo
                        {
                            DeviceId = deviceId,
                            DeviceType = deviceType,
                            DeviceName = deviceName,
                        };
                    }
                    else
                        device.DeviceType = deviceType;

                    if (!device.IsActive)
                    {
                        AddDeviceEvent(new()
                        {
                            Timestamp = firstTimestamp,
                            DeviceId = deviceId,
                            EventType = EventTypes.Connected
                        });
                    }
                    UpsertDevice(device, isActive: true);
                }
                else
                {
                    _recentlyInsertedDevices[deviceId] = (keyboardIncrement, mouseIncrement, DateTime.Now);
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
                _recentlyInsertedDevices.TryRemove(deviceId, out _);

                DeviceInfo? device = _dataService.GetDevice(deviceId) ?? throw new Exception($"Removed device does not exist");
                if (device.IsActive)
                {
                    AddDeviceEvent(new()
                    {
                        DeviceId = device.DeviceId,
                        EventType = EventTypes.Disconnected
                    });
                    UpsertDevice(device, isActive: false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ERROR in DeviceRemovedEvent: {ex.Message}");
            }
        }

        private string? ExtractDeviceId(ManagementBaseObject? obj)
        {
            if (obj == null) return null;

            string? hidDeviceId = obj.GetPropertyValue("DeviceID")?.ToString();
            if (string.IsNullOrEmpty(hidDeviceId)) return null;

            string? vid = ExtractValueFromDeviceId(hidDeviceId, "VID_");
            string? pid = ExtractValueFromDeviceId(hidDeviceId, "PID_");
            if (string.IsNullOrEmpty(vid) || string.IsNullOrEmpty(pid)) return null;

            return $"USB\\VID_{vid}&PID_{pid}";
        }

        private static string ExtractValueFromDeviceId(string? deviceId, string identifier)
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

        private void StartMonitoring()
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

        private void SetCurrentDevicesFromSystem()
        {
            try
            {
                AddDeviceEvent(new() { EventType = EventTypes.AppStarted });

                var devicesById = new Dictionary<string, List<ManagementBaseObject>>();
                ManagementObjectSearcher searcher = new(@"
                    SELECT * FROM Win32_PnPEntity 
                    WHERE Service = 'kbdhid' OR Service = 'mouhid'
                ");

                foreach (ManagementBaseObject obj in searcher.Get())
                {
                    string? deviceId = ExtractDeviceId(obj);
                    if (string.IsNullOrEmpty(deviceId))
                        continue;
                    if (!devicesById.ContainsKey(deviceId))
                        devicesById[deviceId] = [];
                    devicesById[deviceId].Add(obj);
                }

                foreach (var kvp in devicesById)
                {
                    var deviceId = kvp.Key;
                    var objects = kvp.Value;
                    var keyboardCount = objects.Count(obj => obj.GetPropertyValue("PNPClass")?.ToString() == "Keyboard");
                    var mouseCount = objects.Count(obj => obj.GetPropertyValue("PNPClass")?.ToString() == "Mouse");
                    var deviceType = keyboardCount > mouseCount ? DeviceTypes.Keyboard : DeviceTypes.Mouse;

                    //Debug.WriteLine($"Counts: {deviceId} {keyboardCount} {mouseCount}");

                    DeviceInfo? currDevice = null;
                    if (DeviceList.Any(d => d.DeviceId == deviceId))
                    {
                        currDevice = DeviceList.First(d => d.DeviceId == deviceId);
                        if (currDevice.DeviceType == DeviceTypes.Unknown)
                        {
                            currDevice.DeviceType = deviceType;
                        }
                    }
                    else
                    {
                        var deviceName = PowershellScripts.GetDeviceName(deviceId) ?? _unknownDeviceName;
                        currDevice = new DeviceInfo 
                        { 
                            DeviceId = deviceId,
                            DeviceName = deviceName,
                            DeviceType = deviceType,
                        };
                    }
                    
                    AddDeviceEvent(new()
                    {
                        DeviceId = currDevice.DeviceId,
                        EventType = EventTypes.ConnectionStarted
                    });
                    UpsertDevice(currDevice, isActive: true);
                }
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

                foreach (var device in DeviceList)
                {
                    if (device.IsActive)
                    {
                        AddDeviceEvent(new()
                        {
                            DeviceId = device.DeviceId,
                            EventType = EventTypes.ConnectionEnded
                        });
                        UpsertDevice(device, isActive: false);
                    }
                    device.PropertyChanged -= Device_PropertyChanged;
                }

                AddDeviceEvent(new() { EventType = EventTypes.AppEnded });

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
