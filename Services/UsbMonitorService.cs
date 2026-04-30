using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Management;
using System.Windows;
using KeyPulse.Configuration;
using KeyPulse.Helpers;
using KeyPulse.Models;
using Serilog;

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

    private static readonly TimeSpan SignalAggregationWindow = TimeSpan.FromSeconds(
        AppConstants.UsbMonitoring.SignalAggregationSeconds
    );

    private bool _disposed;
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
            TimeSpan.FromSeconds(AppConstants.UsbMonitoring.HeartbeatIntervalSeconds),
            TimeSpan.FromSeconds(AppConstants.UsbMonitoring.HeartbeatIntervalSeconds)
        );
    }

    /// <summary>
    /// Performs the slow startup work off the UI thread: WMI device snapshot and watcher setup.
    /// Must be called once after construction, before the app is considered ready.
    /// Gracefully handles failures: if WMI snapshot fails, continues with known devices;
    /// if WMI monitoring fails to start, logs warning but keeps app running.
    /// </summary>
    public async Task StartAsync()
    {
        // SetCurrentDevicesFromSystem does WMI queries and registry-based device-name lookups
        // which can take 1-3 seconds — run on a thread pool thread to keep the UI responsive.
        // Internal Dispatcher.Invoke calls marshal UI work back to the UI thread safely.
        var snapshotStopwatch = Stopwatch.StartNew();
        Log.Information("Initial devices snapshot started");
        try
        {
            await Task.Run(SetCurrentDevicesFromSystem);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Initial snapshot failed; continuing with existing data");
        }
        finally
        {
            snapshotStopwatch.Stop();
            Log.Information(
                "Initial devices snapshot completed in {ElapsedMs}ms",
                snapshotStopwatch.ElapsedMilliseconds
            );
        }

        var usbMonitoringStopwatch = Stopwatch.StartNew();
        Log.Information("USB monitoring started");
        try
        {
            StartMonitoring();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "USB monitoring setup failed; running in degraded mode");
        }
        finally
        {
            usbMonitoringStopwatch.Stop();
            Log.Information("USB monitoring completed in {ElapsedMs}ms", usbMonitoringStopwatch.ElapsedMilliseconds);
        }
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
        trackedDevice.LastSeenAt = deviceEvent.EventTime;

        if (deviceEvent.EventType.IsOpeningEvent())
        {
            trackedDevice.SessionStartedAt = deviceEvent.EventTime;
            trackedDevice.UpdateLastConnectedAt(
                deviceEvent.EventTime,
                deviceEvent.EventType,
                _dataService.GetEventsFromLastCompletedSession()
            );
        }
        else if (deviceEvent.EventType.IsClosingEvent())
        {
            trackedDevice.CommitSessionUsage(deviceEvent.EventTime);
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
            Log.Error(ex, "Insert event handling failed");
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
            var deviceType = UsbDeviceClassifier.ResolveDeviceType(keyboardSignals, mouseSignals);
            var deviceName = DeviceNameLookup.GetDeviceName(deviceId) ?? AppConstants.UsbMonitoring.UnknownDeviceName;

            if (device == null)
                device = new Device
                {
                    DeviceId = deviceId,
                    DeviceType = deviceType,
                    DeviceName = deviceName,
                };
            else if (IsUnknownDeviceName(device.DeviceName))
                device.DeviceName = deviceName;

            var connectedEvent = new DeviceEvent
            {
                EventTime = firstTimestamp,
                DeviceId = deviceId,
                EventType = EventTypes.Connected,
            };
            AddDeviceEvent(connectedEvent, device);
            _cachedDevices.TryRemove(deviceId, out _);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Cached signal processing failed for {DeviceId}", deviceId);
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
                EventTime = DateTime.Now,
            };
            AddDeviceEvent(disconnectedEvent, device);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Removal event handling failed");
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
        Log.Information("USB WMI watchers started");
    }

    private void SetCurrentDevicesFromSystem()
    {
        AppSessionStartedAt = DateTime.Now;
        AddDeviceEvent(new DeviceEvent { EventType = EventTypes.AppStarted, EventTime = AppSessionStartedAt });

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

        Log.Information("Detected {DeviceCount} connected devices during startup", devicesById.Count);

        foreach (var (deviceId, objects) in devicesById)
        {
            var keyboardSignals = objects.Count(obj =>
                UsbDeviceClassifier.GetInterfaceSignal(obj) == DeviceTypes.Keyboard
            );
            var mouseSignals = objects.Count(obj => UsbDeviceClassifier.GetInterfaceSignal(obj) == DeviceTypes.Mouse);

            var currDevice = DeviceList.FirstOrDefault(d => d.DeviceId == deviceId);
            var deviceType = UsbDeviceClassifier.ResolveDeviceType(keyboardSignals, mouseSignals);
            var deviceName = DeviceNameLookup.GetDeviceName(deviceId) ?? AppConstants.UsbMonitoring.UnknownDeviceName;

            if (currDevice == null)
                currDevice = new Device
                {
                    DeviceId = deviceId,
                    DeviceType = deviceType,
                    DeviceName = deviceName,
                };
            else if (IsUnknownDeviceName(currDevice.DeviceName))
                currDevice.DeviceName = deviceName;

            var connectionStartedEvent = new DeviceEvent
            {
                DeviceId = currDevice.DeviceId,
                EventType = EventTypes.ConnectionStarted,
                EventTime = AppSessionStartedAt,
            };
            AddDeviceEvent(connectionStartedEvent, currDevice);
        }
    }

    private static bool IsUnknownDeviceName(string? deviceName)
    {
        return string.IsNullOrWhiteSpace(deviceName)
            || string.Equals(
                deviceName,
                AppConstants.UsbMonitoring.UnknownDeviceName,
                StringComparison.OrdinalIgnoreCase
            );
    }

    public void Dispose()
    {
        if (_disposed)
        {
            Log.Debug("USB monitoring dispose skipped because it was already disposed");
            return;
        }

        _disposed = true;

        Log.Information("USB monitoring shutdown started");
        var shutdownStopwatch = Stopwatch.StartNew();

        try
        {
            _heartbeatTimer.Dispose();
            Log.Debug("Heartbeat timer disposed");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to dispose heartbeat timer");
        }

        try
        {
            HeartbeatFile.Clear();
            Log.Debug("Heartbeat file cleared");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to clear heartbeat file");
        }

        var sessionTimestamp = DateTime.Now;
        var disconnectedDeviceCount = 0;

        foreach (var device in DeviceList)
        {
            if (device.IsConnected)
            {
                var connectionEndedEvent = new DeviceEvent
                {
                    DeviceId = device.DeviceId,
                    EventType = EventTypes.ConnectionEnded,
                    EventTime = sessionTimestamp,
                };
                AddDeviceEvent(connectionEndedEvent, device);
                disconnectedDeviceCount++;
            }

            device.PropertyChanged -= Device_PropertyChanged;
        }

        if (disconnectedDeviceCount > 0)
            Log.Information(
                "Closed {DisconnectedDeviceCount} open connections during shutdown",
                disconnectedDeviceCount
            );

        AddDeviceEvent(new DeviceEvent { EventType = EventTypes.AppEnded, EventTime = sessionTimestamp });

        try
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
            Log.Information("USB WMI watchers stopped and disposed");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to stop USB WMI watchers");
        }

        shutdownStopwatch.Stop();
        Log.Information("USB monitoring shutdown completed in {ElapsedMs}ms", shutdownStopwatch.ElapsedMilliseconds);
    }
}
