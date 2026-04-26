using System.Windows.Media;
using KeyPulse.Helpers;
using KeyPulse.Models;
using OxyPlot;
using OxyPlot.Series;

namespace KeyPulse.ViewModels.Dashboard;

/// <summary>
/// Builds dashboard pie charts and related tracker behavior.
/// </summary>
internal static class DashboardPieChartBuilder
{
    /// <summary>
    /// Creates a usage-share pie model for a device category (keyboard or mouse).
    /// </summary>
    public static PlotModel BuildUsagePiePlot(
        string title,
        IEnumerable<Device> devices,
        IReadOnlyDictionary<string, double> usageMinutesByDevice
    )
    {
        var model = new PlotModel { Title = title };

        var slices = devices
            .Select(d =>
            {
                usageMinutesByDevice.TryGetValue(d.DeviceId, out var usageMinutes);
                return new DeviceUsageSlice
                {
                    Name = d.DeviceName,
                    Value = usageMinutes,
                    UsageDisplay = TimeFormatter.FormatDuration(TimeSpan.FromMinutes(usageMinutes)),
                    StatusTag = d.IsConnected ? "Connected" : "Disconnected",
                    IsConnected = d.IsConnected,
                    ConnectionTimeLabel = d.IsConnected ? "Last connected" : "Last seen",
                    ConnectionTimeDisplay = d.IsConnected ? d.LastConnectedRelative : d.LastSeenRelative,
                };
            })
            .Where(s => s.Value > 0)
            .OrderByDescending(s => s.Value)
            .ToList();

        if (slices.Count == 0)
        {
            model.Series.Add(
                new PieSeries
                {
                    StrokeThickness = 1,
                    InsideLabelPosition = 0.7,
                    AngleSpan = 360,
                    StartAngle = 0,
                    TrackerFormatString = "No usage data yet.",
                    Slices = { new PieSlice("No data", 1) },
                }
            );
            return model;
        }

        var total = slices.Sum(s => s.Value);
        foreach (var slice in slices)
            slice.ShareDisplay = total > 0 ? $"{slice.Value / total:P1}" : "N/A";

        var series = new PieSeries
        {
            StrokeThickness = 1,
            InsideLabelPosition = 0.7,
            AngleSpan = 360,
            StartAngle = 0,
            TrackerFormatString =
                "{Label}\n"
                + "Status: {StatusTag}\n"
                + "Usage: {UsageDisplay}\n"
                + "Share: {ShareDisplay}\n"
                + "{ConnectionTimeLabel}: {ConnectionTimeDisplay}",
        };

        foreach (var slice in slices)
        {
            var pieSlice = new DashboardPieSlice(slice.Name, slice.Value)
            {
                UsageDisplay = slice.UsageDisplay,
                ShareDisplay = slice.ShareDisplay,
                StatusTag = slice.StatusTag,
                IsConnected = slice.IsConnected,
                ConnectionTimeLabel = slice.ConnectionTimeLabel,
                ConnectionTimeDisplay = slice.ConnectionTimeDisplay,
            };
            series.Slices.Add(pieSlice);
        }

        model.Series.Add(series);
        return model;
    }

    /// <summary>
    /// Creates an interaction controller that shows trackers on hover.
    /// </summary>
    public static IPlotController BuildPieHoverController()
    {
        var controller = new PlotController();
        controller.UnbindAll();
        controller.Bind(new OxyMouseEnterGesture(OxyModifierKeys.None), PlotCommands.HoverTrack);
        controller.Bind(new OxyMouseDownGesture(OxyMouseButton.Left, OxyModifierKeys.None, 1), PlotCommands.HoverTrack);
        return controller;
    }

    /// <summary>
    /// Connects tracker hit events to the dashboard hover preview state.
    /// </summary>
    public static void AttachTrackerPreview(PlotModel model, Action<DashboardPieSlice> onSliceHovered)
    {
#pragma warning disable CS0618
        model.TrackerChanged += (_, e) =>
#pragma warning restore CS0618
        {
            if (e.HitResult?.Item is DashboardPieSlice slice)
                onSliceHovered(slice);
        };
    }

    /// <summary>
    /// Internal mutable slice view model used while preparing pie series data.
    /// </summary>
    private sealed class DeviceUsageSlice
    {
        public required string Name { get; init; }
        public double Value { get; init; }
        public required string UsageDisplay { get; init; }
        public string ShareDisplay { get; set; } = "N/A";
        public required string StatusTag { get; init; }
        public required bool IsConnected { get; init; }
        public required string ConnectionTimeLabel { get; init; }
        public required string ConnectionTimeDisplay { get; init; }
    }
}

/// <summary>
/// Tracker payload attached to each pie slice for rich hover text.
/// </summary>
internal sealed class DashboardPieSlice(string label, double value) : PieSlice(label, value)
{
    public string UsageDisplay { get; init; } = "N/A";
    public string ShareDisplay { get; init; } = "N/A";
    public string StatusTag { get; init; } = "N/A";
    public bool IsConnected { get; init; }
    public string ConnectionTimeLabel { get; init; } = "N/A";
    public string ConnectionTimeDisplay { get; init; } = "N/A";
}

/// <summary>
/// UI-bound hover state displayed beneath the dashboard pie charts.
/// </summary>
internal sealed class DashboardHoverPreview : ObservableObject
{
    private string _deviceName = "Hover a slice to inspect device metadata.";
    private string _statusTag = "Unknown";
    private Brush _statusBrush = Brushes.Gray;
    private string _usageDisplay = "Usage: N/A";
    private string _shareDisplay = "Share: N/A";
    private string _connectionText = "Last seen: N/A";

    public string DeviceName
    {
        get => _deviceName;
        private set
        {
            _deviceName = value;
            OnPropertyChanged();
        }
    }

    public string StatusTag
    {
        get => _statusTag;
        private set
        {
            _statusTag = value;
            OnPropertyChanged();
        }
    }

    public Brush StatusBrush
    {
        get => _statusBrush;
        private set
        {
            _statusBrush = value;
            OnPropertyChanged();
        }
    }

    public string UsageDisplay
    {
        get => _usageDisplay;
        private set
        {
            _usageDisplay = value;
            OnPropertyChanged();
        }
    }

    public string ShareDisplay
    {
        get => _shareDisplay;
        private set
        {
            _shareDisplay = value;
            OnPropertyChanged();
        }
    }

    public string ConnectionText
    {
        get => _connectionText;
        private set
        {
            _connectionText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Copies values from the hovered pie slice into the preview state.
    /// </summary>
    public void UpdateFromSlice(DashboardPieSlice slice)
    {
        DeviceName = slice.Label;
        StatusTag = slice.StatusTag;
        StatusBrush = slice.IsConnected ? Brushes.ForestGreen : Brushes.IndianRed;
        UsageDisplay = $"Usage: {slice.UsageDisplay}";
        ShareDisplay = $"Share: {slice.ShareDisplay}";
        ConnectionText = $"{slice.ConnectionTimeLabel}: {slice.ConnectionTimeDisplay}";
    }
}
