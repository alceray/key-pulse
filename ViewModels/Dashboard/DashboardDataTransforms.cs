using KeyPulse.Models;

namespace KeyPulse.ViewModels.Dashboard;

/// <summary>
/// Resolves dashboard time-range selections to concrete start timestamps.
/// </summary>
internal static class DashboardRangeResolver
{
    /// <summary>Supported range filters shown in the dashboard toolbar.</summary>
    public static readonly IReadOnlyList<string> RangeOptions = ["1 Day", "1 Week", "1 Month", "1 Year", "All Time"];

    /// <summary>
    /// Converts a selected range label to an optional range start.
    /// Returns <c>null</c> for all-time queries.
    /// </summary>
    public static DateTime? ResolveRangeStart(string selectedRange, DateTime now)
    {
        return selectedRange switch
        {
            "1 Day" => now.AddDays(-1),
            "1 Week" => now.AddDays(-7),
            "1 Month" => now.AddMonths(-1),
            "1 Year" => now.AddYears(-1),
            "All Time" => null,
            _ => now.AddDays(-7),
        };
    }
}

/// <summary>
/// Computes dashboard usage totals from device lifecycle events.
/// </summary>
internal static class DashboardUsageCalculator
{
    /// <summary>
    /// Calculates per-device usage minutes within the requested range by pairing opening and closing events.
    /// Open sessions are clipped to <paramref name="rangeEnd"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, double> ComputeUsageMinutesByDevice(
        IReadOnlyList<DeviceEvent> events,
        DateTime? rangeStart,
        DateTime rangeEnd
    )
    {
        var openByDevice = new Dictionary<string, DateTime>();
        var usageByDevice = new Dictionary<string, double>();

        foreach (var deviceEvent in events)
        {
            if (deviceEvent.EventType.IsOpeningEvent())
            {
                openByDevice[deviceEvent.DeviceId] = deviceEvent.EventTime;
                continue;
            }

            if (!deviceEvent.EventType.IsClosingEvent())
                continue;

            if (!openByDevice.TryGetValue(deviceEvent.DeviceId, out var startTime))
                continue;

            AddIntervalUsage(
                usageByDevice,
                deviceEvent.DeviceId,
                startTime,
                deviceEvent.EventTime,
                rangeStart,
                rangeEnd
            );
            openByDevice.Remove(deviceEvent.DeviceId);
        }

        foreach (var (deviceId, startTime) in openByDevice)
            AddIntervalUsage(usageByDevice, deviceId, startTime, rangeEnd, rangeStart, rangeEnd);

        return usageByDevice;
    }

    /// <summary>
    /// Adds a clipped interval duration to the target usage accumulator.
    /// </summary>
    private static void AddIntervalUsage(
        IDictionary<string, double> usageByDevice,
        string deviceId,
        DateTime intervalStart,
        DateTime intervalEnd,
        DateTime? rangeStart,
        DateTime rangeEnd
    )
    {
        if (intervalEnd <= intervalStart)
            return;

        var start = rangeStart.HasValue && intervalStart < rangeStart.Value ? rangeStart.Value : intervalStart;
        var end = intervalEnd > rangeEnd ? rangeEnd : intervalEnd;

        if (end <= start)
            return;

        var usageMinutes = (end - start).TotalMinutes;
        usageByDevice[deviceId] = usageByDevice.TryGetValue(deviceId, out var existing)
            ? existing + usageMinutes
            : usageMinutes;
    }
}
