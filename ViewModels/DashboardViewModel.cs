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
            OnPropertyChanged(nameof(KeystrokesLabel));
            OnPropertyChanged(nameof(MouseClicksLabel));
            Refresh();
        }
    }

    public string KeystrokesLabel => $"Keystrokes ({SelectedRange})";

    public string MouseClicksLabel => $"Mouse Clicks ({SelectedRange})";

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

    public int ActiveDevices
    {
        get => _activeDevices;
        private set
        {
            _activeDevices = value;
            OnPropertyChanged();
        }
    }

    public int Keystrokes24h
    {
        get => _keystrokes24h;
        private set
        {
            _keystrokes24h = value;
            OnPropertyChanged();
        }
    }

    public int MouseClicks24h
    {
        get => _mouseClicks24h;
        private set
        {
            _mouseClicks24h = value;
            OnPropertyChanged();
        }
    }

    public TimeSpan TotalUsageAllTime
    {
        get => _totalUsageAllTime;
        private set
        {
            _totalUsageAllTime = value;
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
    private int _activeDevices;
    private int _keystrokes24h;
    private int _mouseClicks24h;
    private TimeSpan _totalUsageAllTime = TimeSpan.Zero;
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

        // Subscribe reactively so ActiveDevices updates the moment a device connects,
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
            UpdateActiveDevicesCount();
    }

    private void DeviceList_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (Device device in e.NewItems)
                device.PropertyChanged += Device_PropertyChanged;

        if (e.OldItems != null)
            foreach (Device device in e.OldItems)
                device.PropertyChanged -= Device_PropertyChanged;

        UpdateActiveDevicesCount();
    }

    private void UpdateActiveDevicesCount()
    {
        ActiveDevices = _usbMonitorService.DeviceList.Count(d => d.IsConnected);
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

        UpdateActiveDevicesCount();
        Keystrokes24h = snapshots.Sum(s => s.Keystrokes);
        MouseClicks24h = snapshots.Sum(s => s.MouseClicks);
        TotalUsageAllTime = TimeSpan.FromTicks(devices.Sum(d => d.TotalUsage.Ticks));

        var usageMinutesByDevice = DashboardUsageCalculator.ComputeUsageMinutesByDevice(events, from, to);

        var keyboardModel = DashboardPieChartBuilder.BuildUsagePiePlot(
            title: $"Keyboard Usage ({SelectedRange})",
            devices.Where(d => d.DeviceType == DeviceTypes.Keyboard),
            usageMinutesByDevice
        );
        var mouseModel = DashboardPieChartBuilder.BuildUsagePiePlot(
            title: $"Mouse Usage ({SelectedRange})",
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
