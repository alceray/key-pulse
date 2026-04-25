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

    public ObservableCollection<Device> DeviceList;
    public ObservableCollection<DeviceEvent> DeviceEventList;
    public DateTime AppSessionStartedAt { get; private set; }

    // on every irl connection, 2-3 events are created within a short timeframe, so this cache of recent devices
    // prevents inserting duplicate events in the db.
    private readonly ConcurrentDictionary<
        string,
        (int KeyboardSignals, int MouseSignals, DateTime FirstTimestamp)
    > _cachedDevices = new();

    private static readonly TimeSpan SignalAggregationWindow = TimeSpan.FromSeconds(1);
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
    }

    /// <summary>
    /// Performs the slow startup work off the UI thread: WMI device snapshot and watcher setup.
    /// Must be called once after construction, before the app is considered ready.
    /// </summary>
    public async Task StartAsync()
    {
        // SetCurrentDevicesFromSystem does WMI queries and PowerShell device-name lookups
        // which can take 1-3 seconds — run on a thread pool thread to keep the UI responsive.
        // Internal Dispatcher.Invoke calls marshal UI work back to the UI thread safely.
        await Task.Run(SetCurrentDevicesFromSystem);
        StartMonitoring();
    }

    private ObservableCollection<Device> GetAllDevices()
    {
        var devices = _dataService.GetAllDevices();
        foreach (var device in devices)
            device.PropertyChanged += Device_PropertyChanged;

        return new ObservableCollection<Device>(devices);
    }

    private void AddDeviceEvent(DeviceEvent deviceEvent, Device? device = null)
    {
        Application.Current.Dispatcher.BeginInvoke(() => DeviceEventList.Add(deviceEvent));
        _dataService.SaveDeviceEvent(deviceEvent);

        // Skip device operations for app-level events
        if (deviceEvent.EventType.IsAppEvent() || device == null)
            return;

        var trackedDevice = device;

        // Always resolve/apply state on the UI-bound DeviceList instance.
        // DataService.GetDevice returns detached objects when using DbContextFactory,
        // so mutating that instance does not update the UI.
        Application.Current.Dispatcher.Invoke(() =>
        {
            var existingDevice = DeviceList.FirstOrDefault(d => d.DeviceId == device.DeviceId);
            if (existingDevice != null)
            {
                trackedDevice = existingDevice;
                return;
            }

            device.PropertyChanged += Device_PropertyChanged;
            DeviceList.Add(device);
            trackedDevice = device;
        });

        // Perform device state management based on event type
        if (deviceEvent.EventType.IsOpeningEvent())
        {
            trackedDevice.SessionStartedAt = deviceEvent.Timestamp;
            trackedDevice.UpdateLastConnectedAt(
                deviceEvent.Timestamp,
                deviceEvent.EventType,
                _dataService.GetEventsFromLastCompletedSession()
            );
        }
        else if (deviceEvent.EventType.IsClosingEvent())
        {
            trackedDevice.CommitSessionUsage(deviceEvent.Timestamp);
        }

        _dataService.SaveDevice(trackedDevice);
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

            int keyboardSignals;
            int mouseSignals;
            DateTime firstTimestamp;

            if (
                _cachedDevices.TryGetValue(deviceId, out var value)
                && DateTime.Now - value.FirstTimestamp <= SignalAggregationWindow
            )
            {
                keyboardSignals = value.KeyboardSignals + keyboardIncrement;
                mouseSignals = value.MouseSignals + mouseIncrement;
                firstTimestamp = value.FirstTimestamp;
            }
            else
            {
                keyboardSignals = keyboardIncrement;
                mouseSignals = mouseIncrement;
                firstTimestamp = DateTime.Now;
            }

            _cachedDevices[deviceId] = (keyboardSignals, mouseSignals, firstTimestamp);

            Task.Delay(SignalAggregationWindow).ContinueWith(_ => ProcessCachedDevice(deviceId));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ERROR in DeviceInsertedEvent: {ex.Message}");
        }
    }

    private void ProcessCachedDevice(string deviceId)
    {
        try
        {
            if (!_cachedDevices.TryGetValue(deviceId, out var cached))
                return;

            var (keyboardSignals, mouseSignals, firstTimestamp) = cached;

            var device = _dataService.GetDevice(deviceId);
            var existingType = device?.DeviceType ?? DeviceTypes.Unknown;
            var deviceType = UsbDeviceClassifier.ResolveDeviceType(keyboardSignals, mouseSignals, existingType);

            if (device == null)
            {
                var deviceName = PowershellScripts.GetDeviceName(deviceId) ?? _unknownDeviceName;
                device = new Device
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
            _cachedDevices.TryRemove(deviceId, out _);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ERROR in ProcessCachedDevice: {ex.Message}");
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

            var latestDeviceEvent = _dataService.GetLastDeviceEvent(deviceId);
            if (latestDeviceEvent?.EventType == EventTypes.Disconnected)
                return;

            var device = _dataService.GetDevice(deviceId) ?? throw new Exception("Removed device does not exist");
            var disconnectedEvent = new DeviceEvent
            {
                DeviceId = device.DeviceId,
                EventType = EventTypes.Disconnected,
                Timestamp = DateTime.Now,
            };
            AddDeviceEvent(disconnectedEvent, device);
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
        if (sender is Device device)
            if (e.PropertyName == nameof(Device.DeviceName))
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
                    currDevice = new Device
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
                if (device.IsConnected)
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
