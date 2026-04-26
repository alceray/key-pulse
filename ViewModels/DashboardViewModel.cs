using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KeyPulse.Helpers;
using KeyPulse.Models;
using KeyPulse.Services;
using KeyPulse.ViewModels.Dashboard;
using OxyPlot;

namespace KeyPulse.ViewModels;

/// <summary>
/// Orchestrates dashboard data refresh, chart models, and live hover metadata.
/// </summary>
public sealed class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly DataService _dataService;
    private readonly UsbMonitorService _usbMonitorService;
    private readonly DispatcherTimer _refreshTimer;

    public ICommand RefreshCommand { get; }

    public IPlotController PieHoverController { get; }

    public IReadOnlyList<string> RangeOptions => DashboardRangeResolver.RangeOptions;
    public IReadOnlyList<int> BucketSizeOptions { get; } = [5, 10, 15, 20, 30];
    public IReadOnlyList<int> SmoothingWindowOptions { get; } = [1, 2, 3, 4, 5];

    public string SelectedRange
    {
        get => _selectedRange;
        set
        {
            if (_selectedRange == value)
                return;

            _selectedRange = value;
            OnPropertyChanged();
            Refresh();
        }
    }

    public int SelectedBucketMinutes
    {
        get => _selectedBucketMinutes;
        set
        {
            if (_selectedBucketMinutes == value)
                return;

            _selectedBucketMinutes = value;
            OnPropertyChanged();
            Refresh();
        }
    }

    public int SelectedSmoothingWindow
    {
        get => _selectedSmoothingWindow;
        set
        {
            if (_selectedSmoothingWindow == value)
                return;

            _selectedSmoothingWindow = value;
            OnPropertyChanged();
            Refresh();
        }
    }

    public PlotModel KeyboardUsagePiePlot
    {
        get => _keyboardUsagePiePlot;
        private set
        {
            _keyboardUsagePiePlot = value;
            OnPropertyChanged();
        }
    }

    public PlotModel MouseUsagePiePlot
    {
        get => _mouseUsagePiePlot;
        private set
        {
            _mouseUsagePiePlot = value;
            OnPropertyChanged();
        }
    }

    public PlotModel InputActivityPlot
    {
        get => _inputActivityPlot;
        private set
        {
            _inputActivityPlot = value;
            OnPropertyChanged();
        }
    }

    public string HoveredDeviceName => _hoverPreview.DeviceName;

    public string HoveredStatusTag => _hoverPreview.StatusTag;

    public Brush HoveredStatusBrush => _hoverPreview.StatusBrush;

    public string HoveredUsageDisplay => _hoverPreview.UsageDisplay;

    public string HoveredShareDisplay => _hoverPreview.ShareDisplay;

    public string HoveredConnectionText => _hoverPreview.ConnectionText;

    public int ConnectedDevices
    {
        get => _connectedDevices;
        private set
        {
            _connectedDevices = value;
            OnPropertyChanged();
        }
    }

    public string ConnectedDevicesBreakdown
    {
        get => _connectedDevicesBreakdown;
        private set
        {
            _connectedDevicesBreakdown = value;
            OnPropertyChanged();
        }
    }

    public string TopKeyboardsSummary
    {
        get => _topKeyboardsSummary;
        private set
        {
            _topKeyboardsSummary = value;
            OnPropertyChanged();
        }
    }

    public string TopMiceSummary
    {
        get => _topMiceSummary;
        private set
        {
            _topMiceSummary = value;
            OnPropertyChanged();
        }
    }

    public string LastUpdatedText
    {
        get => _lastUpdatedText;
        private set
        {
            _lastUpdatedText = value;
            OnPropertyChanged();
        }
    }

    private PlotModel _keyboardUsagePiePlot = new();
    private PlotModel _mouseUsagePiePlot = new();
    private PlotModel _inputActivityPlot = new();
    private int _connectedDevices;
    private string _connectedDevicesBreakdown = "0 keyboards, 0 mice";
    private string _topKeyboardsSummary = "1. -\n2. -\n3. -";
    private string _topMiceSummary = "1. -\n2. -\n3. -";
    private string _lastUpdatedText = "";
    private string _selectedRange = "1 Week";
    private int _selectedBucketMinutes = 10;
    private int _selectedSmoothingWindow = 2;
    private readonly DashboardHoverPreview _hoverPreview = new();

    public DashboardViewModel(DataService dataService, UsbMonitorService usbMonitorService)
    {
        _dataService = dataService;
        _usbMonitorService = usbMonitorService;
        PieHoverController = DashboardPieChartBuilder.BuildPieHoverController();
        _hoverPreview.PropertyChanged += HoverPreview_PropertyChanged;

        RefreshCommand = new RelayCommand(_ => Refresh());

        // Subscribe reactively so ConnectedDevices updates the moment a device connects,
        // even if the timer hasn't fired yet (fixes the 0-on-startup issue).
        _usbMonitorService.DeviceList.CollectionChanged += DeviceList_CollectionChanged;
        foreach (var device in _usbMonitorService.DeviceList)
            device.PropertyChanged += Device_PropertyChanged;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();

        Refresh();
    }

    /// <summary>
    /// Forwards hover-preview property changes to the view model surface used by XAML bindings.
    /// </summary>
    private void HoverPreview_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DashboardHoverPreview.DeviceName):
                OnPropertyChanged(nameof(HoveredDeviceName));
                break;
            case nameof(DashboardHoverPreview.StatusTag):
                OnPropertyChanged(nameof(HoveredStatusTag));
                break;
            case nameof(DashboardHoverPreview.StatusBrush):
                OnPropertyChanged(nameof(HoveredStatusBrush));
                break;
            case nameof(DashboardHoverPreview.UsageDisplay):
                OnPropertyChanged(nameof(HoveredUsageDisplay));
                break;
            case nameof(DashboardHoverPreview.ShareDisplay):
                OnPropertyChanged(nameof(HoveredShareDisplay));
                break;
            case nameof(DashboardHoverPreview.ConnectionText):
                OnPropertyChanged(nameof(HoveredConnectionText));
                break;
        }
    }

    // Called immediately when any device's IsConnected changes.
    private void Device_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Device.IsConnected))
            Refresh();
    }

    private void DeviceList_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (Device device in e.NewItems)
                device.PropertyChanged += Device_PropertyChanged;

        if (e.OldItems != null)
            foreach (Device device in e.OldItems)
                device.PropertyChanged -= Device_PropertyChanged;
    }

    private void UpdateConnectedDevicesCount()
    {
        var connectedDevices = _usbMonitorService.DeviceList.Where(d => d.IsConnected).ToList();
        ConnectedDevices = connectedDevices.Count;

        var activeKeyboards = connectedDevices.Count(d => d.DeviceType == DeviceTypes.Keyboard);
        var activeMice = connectedDevices.Count(d => d.DeviceType == DeviceTypes.Mouse);
        var keyboardWord = activeKeyboards == 1 ? "keyboard" : "keyboards";
        var mouseWord = activeMice == 1 ? "mouse" : "mice";
        ConnectedDevicesBreakdown = $"{activeKeyboards} {keyboardWord}, {activeMice} {mouseWord}";
    }

    /// <summary>
    /// Rebuilds dashboard metrics and chart models for the current range and chart settings.
    /// </summary>
    public void Refresh()
    {
        var now = DateTime.Now;
        var from = DashboardRangeResolver.ResolveRangeStart(SelectedRange, now);
        var to = now;

        var snapshots = _dataService.GetActivitySnapshots(from: from, to: to).ToList();
        var dashboardEvents = _dataService.GetDashboardEvents(to);
        var events = dashboardEvents.DeviceEvents;

        var devices = _usbMonitorService.DeviceList.ToList();

        UpdateConnectedDevicesCount();
        TopKeyboardsSummary = BuildTopUsageSummary(devices, DeviceTypes.Keyboard);
        TopMiceSummary = BuildTopUsageSummary(devices, DeviceTypes.Mouse);

        var usageMinutesByDevice = DashboardUsageCalculator.ComputeUsageMinutesByDevice(events, from, to);

        var keyboardModel = DashboardPieChartBuilder.BuildUsagePiePlot(
            $"Keyboard Usage ({SelectedRange})",
            devices.Where(d => d.DeviceType == DeviceTypes.Keyboard),
            usageMinutesByDevice
        );
        var mouseModel = DashboardPieChartBuilder.BuildUsagePiePlot(
            $"Mouse Usage ({SelectedRange})",
            devices.Where(d => d.DeviceType == DeviceTypes.Mouse),
            usageMinutesByDevice
        );

        DashboardPieChartBuilder.AttachTrackerPreview(keyboardModel, _hoverPreview.UpdateFromSlice);
        DashboardPieChartBuilder.AttachTrackerPreview(mouseModel, _hoverPreview.UpdateFromSlice);

        KeyboardUsagePiePlot = keyboardModel;
        MouseUsagePiePlot = mouseModel;
        InputActivityPlot = DashboardActivityChartBuilder.BuildInputActivityPlot(
            snapshots,
            dashboardEvents.AppLifecycleEvents,
            from,
            to,
            SelectedRange,
            SelectedBucketMinutes,
            SelectedSmoothingWindow
        );

        LastUpdatedText = $"Last updated: {now:yyyy-MM-dd HH:mm:ss}";
    }

    private static string BuildTopUsageSummary(IEnumerable<Device> devices, DeviceTypes type)
    {
        var ranked = devices
            .Where(d => d.DeviceType == type)
            .OrderByDescending(d => d.TotalUsage)
            .ThenBy(d => d.DeviceName)
            .Take(3)
            .ToList();

        var lines = new List<string>(3);
        for (var i = 0; i < 3; i++)
        {
            if (i >= ranked.Count)
            {
                lines.Add($"{i + 1}. -");
                continue;
            }

            var device = ranked[i];
            lines.Add($"{i + 1}. {device.DeviceName}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Stops timers and unsubscribes listeners to avoid leaks when the dashboard view is unloaded.
    /// </summary>
    public void Dispose()
    {
        _refreshTimer.Stop();
        _hoverPreview.PropertyChanged -= HoverPreview_PropertyChanged;

        _usbMonitorService.DeviceList.CollectionChanged -= DeviceList_CollectionChanged;
        foreach (var device in _usbMonitorService.DeviceList)
            device.PropertyChanged -= Device_PropertyChanged;
    }
}
