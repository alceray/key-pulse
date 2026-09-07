using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;
using KeyPulse.Configuration;
using KeyPulse.Helpers;
using KeyPulse.Models;
using KeyPulse.Services;
using KeyPulse.ViewModels.Dashboard;
using OxyPlot;
using OxyPlot.Series;

namespace KeyPulse.ViewModels;

/// <summary>
/// Orchestrates dashboard data refresh, chart models, and live hover metadata.
/// </summary>
public sealed class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly DataService _dataService;
    private readonly DailyStatsService _dailyStatsService;
    private readonly UsbMonitorService _usbMonitorService;
    private readonly AppTimerService _appTimerService;
    private readonly RawInputService _rawInputService;
    private readonly ThemeService _themeService;

    public ICommand RefreshCommand { get; }

    public ICommand ToggleTrackingPauseCommand { get; }

    /// <summary>
    /// Session-only input-capture pause state, mirrored from the shared service so the dashboard control
    /// and the tray menu stay in sync. Never persisted; capture resumes on the next app start.
    /// </summary>
    public bool IsTrackingPaused
    {
        get => _isTrackingPaused;
        private set
        {
            if (_isTrackingPaused == value)
                return;

            _isTrackingPaused = value;
            OnPropertyChanged();
        }
    }

    public IPlotController PieHoverController { get; }

    public IPlotController ActivityChartController { get; }

    public IReadOnlyList<string> RangeOptions => DashboardRangeResolver.RangeOptions;

    public string SelectedRange
    {
        get => _selectedRange;
        set
        {
            if (_selectedRange == value)
                return;

            _selectedRange = value;
            OnPropertyChanged();
            Refresh(resetActivityView: true);
        }
    }

    public PlotModel KeyboardPiePlot
    {
        get => _keyboardPiePlot;
        private set
        {
            _keyboardPiePlot = value;
            OnPropertyChanged();
        }
    }

    public PlotModel MousePiePlot
    {
        get => _mousePiePlot;
        private set
        {
            _mousePiePlot = value;
            OnPropertyChanged();
        }
    }

    // Persistent model mutated in place each refresh so the PlotView keeps its axes (and pan/zoom state).
    public PlotModel InputActivityPlot => _inputActivityPlot;

    public string HoveredDeviceName => _hoverPreview.DeviceName;

    public string HoveredStatusTag => _hoverPreview.StatusTag;

    public Brush HoveredStatusBrush => _hoverPreview.StatusBrush;

    public string HoveredConnectedTime => _hoverPreview.ConnectedTime;

    public string HoveredPercentage => _hoverPreview.Percentage;

    public string HoveredLastSeenOrConnected => _hoverPreview.LastSeenOrConnected;

    public string HoveredGroupedDevices => _hoverPreview.GroupedDevices;

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

    public string RangeDisplayText
    {
        get => _rangeDisplayText;
        private set
        {
            _rangeDisplayText = value;
            OnPropertyChanged();
        }
    }

    private PlotModel _keyboardPiePlot = new();
    private PlotModel _mousePiePlot = new();
    private PlotModel _inputActivityPlot = new();
    private int _connectedDevices;
    private string _connectedDevicesBreakdown = "0 keyboards, 0 mice";
    private string _topKeyboardsSummary = "1. -\n2. -\n3. -";
    private string _topMiceSummary = "1. -\n2. -\n3. -";
    private string _lastUpdatedText = "";
    private string _rangeDisplayText = "";
    private bool _isTrackingPaused;
    private string _selectedRange = DashboardRangeResolver.DefaultRange;
    private readonly DashboardHoverPreview _hoverPreview = new();
    private readonly DashboardDeviceColorPalette _deviceColorPalette = new();
    private int _refreshScheduled;
    private int _refreshRunning;

    // Set on range switch / first load to refit the activity chart; consumed once on the next apply.
    private int _pendingActivityViewReset;

    private string? _selectedDeviceId;

    // Snapshot of the inputs from the last data refresh, so a selection click can rebuild the plots
    // (emphasize selected / fade the rest) without re-querying the DB. Replaced as a unit on each refresh.
    private DashboardRenderInputs? _renderInputs;

    private sealed record DashboardRenderInputs(
        IReadOnlyList<Device> Devices,
        IReadOnlyList<ActivitySnapshot> Snapshots,
        IReadOnlyList<DeviceEvent> AppLifecycleEvents,
        IReadOnlyDictionary<string, double> ConnectionMinutes,
        IReadOnlyDictionary<string, OxyColor> Colors,
        DateTime? From,
        DateTime To
    );

    public DashboardViewModel(
        DataService dataService,
        DailyStatsService dailyStatsService,
        UsbMonitorService usbMonitorService,
        AppTimerService appTimerService,
        RawInputService rawInputService,
        ThemeService themeService
    )
    {
        _dataService = dataService;
        _dailyStatsService = dailyStatsService;
        _usbMonitorService = usbMonitorService;
        _appTimerService = appTimerService;
        _rawInputService = rawInputService;
        _themeService = themeService;
        _themeService.ThemeChanged += OnThemeChanged;
        PieHoverController = DashboardPieChartBuilder.BuildPieHoverController(HandlePlotClick);
        ActivityChartController = DashboardActivityChartBuilder.BuildActivityChartController(HandlePlotClick);
        _hoverPreview.PropertyChanged += HoverPreview_PropertyChanged;

        RefreshCommand = new RelayCommand(_ => Refresh());
        ToggleTrackingPauseCommand = new RelayCommand(_ => _rawInputService.TogglePause());
        _isTrackingPaused = _rawInputService.IsPaused;
        _rawInputService.PauseStateChanged += OnPauseStateChanged;

        // Subscribe reactively so ConnectedDevices updates the moment a device connects,
        // even if the timer hasn't fired yet (fixes the 0-on-startup issue).
        _usbMonitorService.DeviceList.CollectionChanged += DeviceList_CollectionChanged;
        foreach (var device in _usbMonitorService.DeviceList)
            device.PropertyChanged += Device_PropertyChanged;

        _appTimerService.ThirtySecondTick += OnRefreshTick;

        Refresh(resetActivityView: true);
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
            case nameof(DashboardHoverPreview.ConnectedTime):
                OnPropertyChanged(nameof(HoveredConnectedTime));
                break;
            case nameof(DashboardHoverPreview.Percentage):
                OnPropertyChanged(nameof(HoveredPercentage));
                break;
            case nameof(DashboardHoverPreview.LastSeenOrConnected):
                OnPropertyChanged(nameof(HoveredLastSeenOrConnected));
                break;
            case nameof(DashboardHoverPreview.GroupedDevices):
                OnPropertyChanged(nameof(HoveredGroupedDevices));
                break;
        }
    }

    // Called immediately when any device's IsConnected changes.
    private void Device_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            e.PropertyName
            is nameof(Device.IsConnected)
                or nameof(Device.IsHiddenFromDisplay)
                or nameof(Device.DeviceType)
        )
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

    private void UpdateConnectedDevicesCount(DataService.DashboardDeviceQueryResult dashboardDevices)
    {
        var connectedDevices = dashboardDevices.Devices.Where(d => d.IsConnected).ToList();
        ConnectedDevices = connectedDevices.Count;

        var activeKeyboards = connectedDevices.Count(d => d.DeviceType == DeviceTypes.Keyboard);
        var activeMice = connectedDevices.Count(d => d.DeviceType == DeviceTypes.Mouse);
        var keyboardWord = activeKeyboards == 1 ? "keyboard" : "keyboards";
        var mouseWord = activeMice == 1 ? "mouse" : "mice";
        ConnectedDevicesBreakdown = $"{activeKeyboards} {keyboardWord}, {activeMice} {mouseWord}";
    }

    /// <summary>
    /// Rebuilds dashboard metrics and chart models for the current range and chart settings.
    /// Database queries and chart construction run on a background thread; only the final
    /// property assignments are marshaled back to the UI thread. Bursty calls are coalesced.
    /// </summary>
    private void Refresh(bool resetActivityView = false)
    {
        if (resetActivityView)
            Interlocked.Exchange(ref _pendingActivityViewReset, 1);

        // Mark a refresh as pending; if one is already running, it will pick up the latest state when it finishes.
        Interlocked.Exchange(ref _refreshScheduled, 1);

        // Only one background refresh in flight at a time.
        if (Interlocked.CompareExchange(ref _refreshRunning, 1, 0) != 0)
            return;

        _ = Task.Run(RunRefreshLoopAsync);
    }

    private async Task RunRefreshLoopAsync()
    {
        try
        {
            while (Interlocked.Exchange(ref _refreshScheduled, 0) == 1)
            {
                await RefreshOnceAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _refreshRunning, 0);

            // If a request landed between the last check and clearing the running flag,
            // restart the loop so we don't drop the latest refresh.
            if (
                Interlocked.CompareExchange(ref _refreshScheduled, 0, 0) == 1
                && Interlocked.CompareExchange(ref _refreshRunning, 1, 0) == 0
            )
            {
                _ = Task.Run(RunRefreshLoopAsync);
            }
        }
    }

    /// <summary>
    /// Picks the chart's snapshot source by range: hour-aligned bucket tiers read per-day hourly
    /// aggregates, so cost scales with the displayed range and pruned history still charts fully;
    /// fine-grained short ranges read the raw minute rows, whose window is naturally bounded.
    /// </summary>
    private List<ActivitySnapshot> LoadChartSnapshots(
        DateTime? from,
        DateTime to,
        IReadOnlyList<Device> devices,
        HashSet<string> visibleDeviceIds,
        IReadOnlyList<DeviceEvent> appLifecycleEvents
    )
    {
        var spanStart = from ?? appLifecycleEvents.Select(e => (DateTime?)e.EventTime).DefaultIfEmpty().Min();
        var rangeSpan = spanStart.HasValue && to > spanStart.Value ? to - spanStart.Value : TimeSpan.Zero;

        if (spanStart.HasValue && DashboardHourlyActivityAdapter.CanServeFromHourlyAggregates(rangeSpan))
        {
            var stats = _dailyStatsService.GetVisibleDailyDeviceStats(
                DateOnly.FromDateTime(spanStart.Value),
                DateOnly.FromDateTime(to)
            );
            var deviceTypesById = devices.ToDictionary(
                d => d.DeviceId,
                d => d.DeviceType,
                StringComparer.OrdinalIgnoreCase
            );
            return DashboardHourlyActivityAdapter
                .ToHourlySnapshots(stats, deviceTypesById)
                .Where(s => visibleDeviceIds.Contains(s.DeviceId))
                .ToList();
        }

        return _dataService
            .GetActivitySnapshots(from: from, to: to)
            .Where(s => visibleDeviceIds.Contains(s.DeviceId))
            .ToList();
    }

    private async Task RefreshOnceAsync()
    {
        var now = DateTime.Now;
        var from = DashboardRangeResolver.ResolveRangeStart(SelectedRange, now);
        var to = now;

        // Heavy work off the UI thread. Events load before snapshots because the all-time range
        // needs the earliest event to decide which snapshot source serves the chart.
        var dashboardDevices = _dataService.GetDashboardDevices();
        var devices = dashboardDevices.Devices;
        var visibleDeviceIds = devices.Select(d => d.DeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dashboardEvents = _dataService.GetDashboardEvents(from, to);
        var visibleDeviceEvents = dashboardEvents
            .DeviceEvents.Where(e => visibleDeviceIds.Contains(e.DeviceId))
            .ToList();
        var snapshots = LoadChartSnapshots(from, to, devices, visibleDeviceIds, dashboardEvents.AppLifecycleEvents);

        var connectionMinutesByDevice = DashboardConnectionTimeCalculator.ComputeConnectionMinutesByDevice(
            visibleDeviceEvents,
            from,
            to
        );
        var colorsByDevice = _deviceColorPalette.GetColorsForDevices(devices, connectionMinutesByDevice);

        var renderInputs = new DashboardRenderInputs(
            devices,
            snapshots,
            dashboardEvents.AppLifecycleEvents,
            connectionMinutesByDevice,
            colorsByDevice,
            from,
            to
        );

        // Preserve the current selection across refreshes; drop it if that device is gone.
        var selectedDeviceId = _selectedDeviceId;
        if (selectedDeviceId != null && !visibleDeviceIds.Contains(selectedDeviceId))
            selectedDeviceId = null;

        var (keyboardModel, mouseModel, activityData) = BuildPlotModels(renderInputs, selectedDeviceId);

        var topKeyboards = BuildTopByConnectionSecondsSummary(dashboardDevices.TopKeyboardsByConnectionSeconds);
        var topMice = BuildTopByConnectionSecondsSummary(dashboardDevices.TopMiceByConnectionSeconds);
        var rangeDisplay = TimeFormatter.FormatDateRange(from, to);
        var lastUpdated = $"Last updated: {now.ToString(AppConstants.Date.DateFormat)}";

        // Apply results on the UI thread.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;

        await dispatcher.InvokeAsync(() =>
        {
            _selectedDeviceId = selectedDeviceId;
            _renderInputs = renderInputs;

            RangeDisplayText = rangeDisplay;
            UpdateConnectedDevicesCount(dashboardDevices);
            TopKeyboardsSummary = topKeyboards;
            TopMiceSummary = topMice;
            KeyboardPiePlot = keyboardModel;
            MousePiePlot = mouseModel;
            var resetActivityView = Interlocked.Exchange(ref _pendingActivityViewReset, 0) == 1;
            DashboardActivityChartBuilder.ApplyInputActivityPlot(_inputActivityPlot, activityData, resetActivityView);
            ApplyPlotThemes();
            LastUpdatedText = lastUpdated;
        });
    }

    /// <summary>
    /// Handles a left-click on any dashboard plot. Clicking a pie slice or activity line selects that device
    /// (emphasized, others faded); clicking the grouped "Others" slice or empty space clears the selection.
    /// </summary>
    private void HandlePlotClick(IPlotView view, OxyMouseDownEventArgs args)
    {
        var model = view.ActualModel;
        string? deviceId = null;

        if (model != null)
        {
            if (model.Series.OfType<PieSeries>().Any())
            {
                var slice = DashboardPieChartBuilder.GetSliceAt(model, args.Position);
                deviceId = slice is { IsOthers: false } ? slice.DeviceId : null;
            }
            else if (
                model.GetSeriesFromPoint(args.Position, DashboardActivityChartBuilder.LineHitTolerance)
                    is LineSeries line
                && line.Tag is string lineDeviceId
            )
            {
                // Nearest series (matching the hover tracker) so selection aligns with the hover text.
                deviceId = lineDeviceId;
            }
        }

        SetSelectedDevice(deviceId);
        args.Handled = true;
    }

    private void SetSelectedDevice(string? deviceId)
    {
        if (_selectedDeviceId == deviceId)
            return;

        _selectedDeviceId = deviceId;
        RenderModelsFromCache();
    }

    /// <summary>Rebuilds the three plot models from cached data for the current selection (no DB query).</summary>
    private void RenderModelsFromCache()
    {
        if (_renderInputs is not { } inputs)
            return;

        var (keyboardModel, mouseModel, activityData) = BuildPlotModels(inputs, _selectedDeviceId);

        KeyboardPiePlot = keyboardModel;
        MousePiePlot = mouseModel;
        DashboardActivityChartBuilder.ApplyInputActivityPlot(_inputActivityPlot, activityData, resetView: false);
        ApplyPlotThemes();
    }

    private void ApplyPlotThemes()
    {
        DashboardPlotTheme.Apply(_keyboardPiePlot);
        DashboardPlotTheme.Apply(_mousePiePlot);
        DashboardPlotTheme.Apply(_inputActivityPlot);
    }

    private void OnThemeChanged()
    {
        RenderModelsFromCache();
        _hoverPreview.RefreshTheme();
    }

    /// <summary>Builds the keyboard pie, mouse pie, and activity render data for a given selection state.</summary>
    private (
        PlotModel Keyboard,
        PlotModel Mouse,
        DashboardActivityChartBuilder.ActivityPlotData Activity
    ) BuildPlotModels(DashboardRenderInputs inputs, string? selectedDeviceId)
    {
        var keyboardModel = DashboardPieChartBuilder.BuildConnectionTimePiePlot(
            "Keyboards",
            inputs.Devices.Where(d => d.DeviceType == DeviceTypes.Keyboard),
            inputs.ConnectionMinutes,
            inputs.Colors,
            selectedDeviceId
        );
        var mouseModel = DashboardPieChartBuilder.BuildConnectionTimePiePlot(
            "Mice",
            inputs.Devices.Where(d => d.DeviceType == DeviceTypes.Mouse),
            inputs.ConnectionMinutes,
            inputs.Colors,
            selectedDeviceId
        );

        DashboardPieChartBuilder.AttachTrackerPreview(keyboardModel, _hoverPreview.UpdateFromSlice);
        DashboardPieChartBuilder.AttachTrackerPreview(mouseModel, _hoverPreview.UpdateFromSlice);

        var activityData = DashboardActivityChartBuilder.ComputeInputActivityPlot(
            inputs.Snapshots,
            inputs.Devices,
            inputs.AppLifecycleEvents,
            inputs.From,
            inputs.To,
            inputs.Colors,
            selectedDeviceId
        );

        return (keyboardModel, mouseModel, activityData);
    }

    /// <summary>
    /// Builds the top-3 all-time summary from the persisted connection-time snapshot.
    /// </summary>
    private static string BuildTopByConnectionSecondsSummary(IReadOnlyList<Device> devices)
    {
        var lines = new List<string>(3);
        for (var i = 0; i < 3; i++)
        {
            if (i >= devices.Count || devices[i].TotalConnectionSeconds <= 0)
            {
                lines.Add($"{i + 1}. -");
                continue;
            }

            lines.Add($"{i + 1}. {devices[i].DeviceName}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Stops timers and unsubscribes listeners during application shutdown.
    /// </summary>
    public void Dispose()
    {
        _themeService.ThemeChanged -= OnThemeChanged;
        _appTimerService.ThirtySecondTick -= OnRefreshTick;
        _hoverPreview.PropertyChanged -= HoverPreview_PropertyChanged;
        _rawInputService.PauseStateChanged -= OnPauseStateChanged;

        _usbMonitorService.DeviceList.CollectionChanged -= DeviceList_CollectionChanged;
        foreach (var device in _usbMonitorService.DeviceList)
            device.PropertyChanged -= Device_PropertyChanged;
    }

    // Mirror pause toggles from any source (this control or the tray). ObservableObject marshals the
    // resulting PropertyChanged to the UI thread, so the icon and tooltip refresh safely.
    private void OnPauseStateChanged(bool paused) => IsTrackingPaused = paused;

    private void OnRefreshTick(object? sender, EventArgs e) => Refresh();
}
