using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Management;
using System.Windows;
using KeyPulse.Helpers;
using KeyPulse.Models;

namespace KeyPulse.Services;

public class UsbMonitorService : IDisposable
{
    private ManagementEventWatcher? _insertWatcher;
    private ManagementEventWatcher? _removeWatcher;

    public ObservableCollection<DeviceInfo> DeviceList;
    public ObservableCollection<DeviceEvent> DeviceEventList;
    public DateTime AppSessionStartedAt { get; private set; }

    // for every irl connection, 2-3 insert events are created within a very short timeframe, so this cache of
    // recently inserted devices helps to prevent inserting duplicate connected events in the db.
    private readonly ConcurrentDictionary<
        string,
        (int KeyboardSignals, int MouseSignals, DateTime FirstTimestamp)
    > _recentlyInsertedDevices = new();

    private readonly string _unknownDeviceName = "Unknown Device";
    private bool _disposed = false;
    private readonly DataService _dataService;
    private readonly Timer _heartbeatTimer;

    public UsbMonitorService(DataService dataService)
    {
        _dataService = dataService;

        // Recover from any previous unclean shutdown before loading events, so the log is consistent from the start.
        _dataService.RecoverFromCrash();
        _dataService.RebuildDeviceSnapshots();

        DeviceList = GetAllDevices();

        DeviceEventList = new ObservableCollection<DeviceEvent>(_dataService.GetAllDeviceEvents());

        // Write heartbeat immediately, then every 30 seconds, so RecoverFromCrash
        // has a recent timestamp if the process is force-killed.
        HeartbeatFile.Write();
        _heartbeatTimer = new Timer(
            _ => HeartbeatFile.Write(),
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30)
        );

        SetCurrentDevicesFromSystem();
        StartMonitoring();
    }

    private static void PrintObject(ManagementBaseObject obj)
    {
        if (obj == null)
            return;

        Debug.WriteLine("OBJECT PROPERTIES");
        foreach (var property in obj.Properties)
        {
            var name = property.Name;
            var value = property.Value;
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(value?.ToString()))
                Debug.WriteLine($"{name}: {value}");
        }
    }

    private ObservableCollection<DeviceInfo> GetAllDevices()
    {
        var devices = _dataService.GetAllDevices();
        foreach (var device in devices)
            device.PropertyChanged += Device_PropertyChanged;

        return new ObservableCollection<DeviceInfo>(devices);
    }

    private void AddDeviceEvent(DeviceEvent deviceEvent, DeviceInfo? device = null)
    {
        Application.Current.Dispatcher.BeginInvoke(() => DeviceEventList.Add(deviceEvent));
        _dataService.AddDeviceEvent(deviceEvent);

        // Skip device operations for app-level events
        if (deviceEvent.EventType.IsAppEvent() || device == null)
            return;

        // Ensure device is in the DeviceList
        if (!DeviceList.Any(d => d.DeviceId == device.DeviceId))
        {
            device.PropertyChanged += Device_PropertyChanged;
            Application.Current.Dispatcher.Invoke(() => DeviceList.Add(device));
        }

        // Perform device state management based on event type
        if (deviceEvent.EventType.IsOpeningEvent())
        {
            device.SessionStartedAt = deviceEvent.Timestamp;
            device.LastConnectedAt ??= deviceEvent.Timestamp;
        }
        else if (deviceEvent.EventType.IsClosingEvent())
        {
            device.SessionStartedAt = null;
        }

        _dataService.SaveDevice(device);
    }

    private void DeviceInsertedEvent(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var instance = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            if (instance == null)
                return;

            //printObj(instance);

            var deviceId = ExtractDeviceId(instance);
            if (string.IsNullOrEmpty(deviceId))
                return;

            var signalType = UsbDeviceClassifier.GetInterfaceSignal(instance);
            var keyboardIncrement = signalType == DeviceTypes.Keyboard ? 1 : 0;
            var mouseIncrement = signalType == DeviceTypes.Mouse ? 1 : 0;

            if (_recentlyInsertedDevices.TryGetValue(deviceId, out var value))
            {
                var (keyboardSignals, mouseSignals, firstTimestamp) = value;
                keyboardSignals += keyboardIncrement;
                mouseSignals += mouseIncrement;
                _recentlyInsertedDevices[deviceId] = (keyboardSignals, mouseSignals, firstTimestamp);

                // wait until we have at least 2 signals to determine device type
                if (keyboardSignals + mouseSignals < 2)
                    return;

                var device = _dataService.GetDevice(deviceId);
                var existingType = device?.DeviceType ?? DeviceTypes.Unknown;
                var deviceType = UsbDeviceClassifier.ResolveDeviceType(keyboardSignals, mouseSignals, existingType);

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

                var connectedEvent = new DeviceEvent
                {
                    Timestamp = firstTimestamp,
                    DeviceId = deviceId,
                    EventType = EventTypes.Connected,
                };
                AddDeviceEvent(connectedEvent, device);
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
        try
        {
            var instance = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            if (instance == null)
                return;

            var deviceId = ExtractDeviceId(instance);
            if (string.IsNullOrEmpty(deviceId))
                return;
            _recentlyInsertedDevices.TryRemove(deviceId, out _);

            var device = _dataService.GetDevice(deviceId) ?? throw new Exception($"Removed device does not exist");
            if (device.IsActive)
            {
                var disconnectedEvent = new DeviceEvent
                {
                    DeviceId = device.DeviceId,
                    EventType = EventTypes.Disconnected,
                    Timestamp = DateTime.Now,
                };
                AddDeviceEvent(disconnectedEvent, device);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ERROR in DeviceRemovedEvent: {ex.Message}");
        }
    }

    private static string? ExtractDeviceId(ManagementBaseObject? obj)
    {
        if (obj == null)
            return null;

        var hidDeviceId = obj.GetPropertyValue("DeviceID")?.ToString();
        if (string.IsNullOrEmpty(hidDeviceId))
            return null;

        var vid = ExtractValueFromDeviceId(hidDeviceId, "VID_");
        var pid = ExtractValueFromDeviceId(hidDeviceId, "PID_");
        if (string.IsNullOrEmpty(vid) || string.IsNullOrEmpty(pid))
            return null;

        return $"USB\\VID_{vid}&PID_{pid}";
    }

    private static string ExtractValueFromDeviceId(string? deviceId, string identifier)
    {
        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(identifier))
            return "";

        var startIndex = deviceId.IndexOf(identifier, StringComparison.OrdinalIgnoreCase);
        if (startIndex < 0)
            return "";
        startIndex += identifier.Length;

        var endIndex = deviceId.IndexOfAny(['&', '\\'], startIndex);
        if (endIndex < 0)
            endIndex = deviceId.Length;

        return deviceId[startIndex..endIndex];
    }

    private void Device_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is DeviceInfo device)
            if (e.PropertyName == nameof(DeviceInfo.DeviceName))
                _dataService.SaveDevice(device);
    }

    private void StartMonitoring()
    {
        if (_disposed)
            return;

        try
        {
            WqlEventQuery insertQuery = new(
                @"
                    SELECT * FROM __InstanceCreationEvent WITHIN 2 
                    WHERE TargetInstance ISA 'Win32_PnPEntity' 
                    AND (TargetInstance.Service = 'kbdhid' OR TargetInstance.Service = 'mouhid')
                "
            );
            _insertWatcher = new ManagementEventWatcher(insertQuery);
            _insertWatcher.EventArrived += DeviceInsertedEvent;
            _insertWatcher.Start();

            WqlEventQuery removeQuery = new(
                @"
                    SELECT * FROM __InstanceDeletionEvent WITHIN 2 
                    WHERE TargetInstance ISA 'Win32_PnPEntity' 
                    AND (TargetInstance.Service = 'kbdhid' OR TargetInstance.Service = 'mouhid')
                "
            );
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
            AppSessionStartedAt = DateTime.Now;
            AddDeviceEvent(new DeviceEvent { EventType = EventTypes.AppStarted, Timestamp = AppSessionStartedAt });

            var devicesById = new Dictionary<string, List<ManagementBaseObject>>();
            ManagementObjectSearcher searcher = new(
                @"
                    SELECT * FROM Win32_PnPEntity 
                    WHERE Service = 'kbdhid' OR Service = 'mouhid'
                "
            );

            foreach (var obj in searcher.Get())
            {
                var deviceId = ExtractDeviceId(obj);
                if (string.IsNullOrEmpty(deviceId))
                    continue;
                if (!devicesById.ContainsKey(deviceId))
                    devicesById[deviceId] = [];
                devicesById[deviceId].Add(obj);
            }

            foreach (var (deviceId, objects) in devicesById)
            {
                var keyboardSignals = objects.Count(obj =>
                    UsbDeviceClassifier.GetInterfaceSignal(obj) == DeviceTypes.Keyboard
                );
                var mouseSignals = objects.Count(obj =>
                    UsbDeviceClassifier.GetInterfaceSignal(obj) == DeviceTypes.Mouse
                );

                var currDevice = DeviceList.FirstOrDefault(d => d.DeviceId == deviceId);
                if (currDevice != null)
                    currDevice.DeviceType = UsbDeviceClassifier.ResolveDeviceType(
                        keyboardSignals,
                        mouseSignals,
                        currDevice.DeviceType
                    );
                else
                    currDevice = new DeviceInfo
                    {
                        DeviceId = deviceId,
                        DeviceName = PowershellScripts.GetDeviceName(deviceId) ?? _unknownDeviceName,
                        DeviceType = UsbDeviceClassifier.ResolveDeviceType(keyboardSignals, mouseSignals),
                    };

                var connectionStartedEvent = new DeviceEvent
                {
                    DeviceId = currDevice.DeviceId,
                    EventType = EventTypes.ConnectionStarted,
                    Timestamp = AppSessionStartedAt,
                };
                AddDeviceEvent(connectionStartedEvent, currDevice);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ERROR in SetCurrentDevicesFromSystem: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            _heartbeatTimer.Dispose();
            HeartbeatFile.Clear();

            var sessionTimestamp = DateTime.Now;
            foreach (var device in DeviceList)
            {
                if (device.IsActive)
                {
                    var connectionEndedEvent = new DeviceEvent
                    {
                        DeviceId = device.DeviceId,
                        EventType = EventTypes.ConnectionEnded,
                        Timestamp = sessionTimestamp,
                    };
                    AddDeviceEvent(connectionEndedEvent, device);
                }

                device.PropertyChanged -= Device_PropertyChanged;
            }

            AddDeviceEvent(new DeviceEvent { EventType = EventTypes.AppEnded, Timestamp = sessionTimestamp });

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

    ~UsbMonitorService()
    {
        Dispose(false);
    }
}
