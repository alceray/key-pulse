using KeyPulse.Helpers;
using KeyPulse.Models;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace KeyPulse.ViewModels.Dashboard;

/// <summary>
/// Builds the dashboard activity plot for keyboard and mouse input totals.
/// </summary>
internal static class DashboardActivityChartBuilder
{
    /// <summary>
    /// Creates a time-series chart from minute snapshots, with configurable time resolution and smoothing.
    /// Buckets outside app-running intervals are forced to zero.
    /// </summary>
    public static PlotModel BuildInputActivityPlot(
        IEnumerable<ActivitySnapshot> snapshots,
        IEnumerable<DeviceEvent> lifecycleEvents,
        DateTime? from,
        DateTime to,
        string rangeLabel,
        int bucketMinutes,
        int smoothingWindow
    )
    {
        var normalizedBucketMinutes = Math.Max(1, bucketMinutes);
        var normalizedSmoothingWindow = Math.Max(1, smoothingWindow);

        var model = new PlotModel { Title = $"Input Activity ({rangeLabel})" };

        model.Axes.Add(
            new DateTimeAxis
            {
                Position = AxisPosition.Bottom,
                StringFormat = "MM-dd HH:mm",
                Title = "Time",
                IntervalType = DateTimeIntervalType.Hours,
            }
        );

        model.Axes.Add(
            new LinearAxis
            {
                Position = AxisPosition.Left,
                Title = "Input Count",
                Minimum = 0,
            }
        );

        var bucketTimeline = BuildBucketTimeline(snapshots, lifecycleEvents, from, to, normalizedBucketMinutes);
        if (bucketTimeline.Count == 0)
            return model;

        var appIntervals = BuildAppRunningIntervals(lifecycleEvents, to);
        var isAppRunningByBucket = bucketTimeline.ToDictionary(
            bucket => bucket,
            bucket =>
                appIntervals.Any(interval =>
                    BucketsOverlap(bucket, interval.Start, interval.End, normalizedBucketMinutes)
                )
        );

        var keyboardByBucket = BuildBucketMetric(
            snapshots,
            from,
            to,
            snapshot => snapshot.Keystrokes,
            isAppRunningByBucket,
            normalizedBucketMinutes
        );
        var mouseByBucket = BuildBucketMetric(
            snapshots,
            from,
            to,
            snapshot => snapshot.MouseClicks + snapshot.MouseMovementSeconds,
            isAppRunningByBucket,
            normalizedBucketMinutes
        );

        keyboardByBucket = SmoothBuckets(
            keyboardByBucket,
            bucketTimeline,
            isAppRunningByBucket,
            normalizedSmoothingWindow
        );
        mouseByBucket = SmoothBuckets(mouseByBucket, bucketTimeline, isAppRunningByBucket, normalizedSmoothingWindow);

        var keyboardSeries = new LineSeries { Title = "Keyboard Inputs (Keystrokes)", StrokeThickness = 2 };
        var mouseSeries = new LineSeries { Title = "Mouse Inputs (Clicks + Movement)", StrokeThickness = 2 };

        foreach (var bucket in bucketTimeline)
        {
            var x = DateTimeAxis.ToDouble(bucket);
            keyboardByBucket.TryGetValue(bucket, out var keyboardValue);
            mouseByBucket.TryGetValue(bucket, out var mouseValue);

            keyboardSeries.Points.Add(new DataPoint(x, keyboardValue));
            mouseSeries.Points.Add(new DataPoint(x, mouseValue));
        }

        model.Series.Add(keyboardSeries);
        model.Series.Add(mouseSeries);

        return model;
    }

    /// <summary>
    /// Builds the full bucket timeline that the chart should render for the selected range.
    /// </summary>
    private static List<DateTime> BuildBucketTimeline(
        IEnumerable<ActivitySnapshot> snapshots,
        IEnumerable<DeviceEvent> lifecycleEvents,
        DateTime? from,
        DateTime to,
        int bucketMinutes
    )
    {
        var firstSnapshotMinute = snapshots.Select(s => s.Minute).DefaultIfEmpty().Min();
        var firstLifecycleMinute = lifecycleEvents.Select(e => e.EventTime).DefaultIfEmpty().Min();

        var inferredStart = MinNonDefault(firstSnapshotMinute, firstLifecycleMinute);
        var start = ToBucketStart(from ?? inferredStart, bucketMinutes);
        var end = ToBucketStart(to, bucketMinutes);

        if (start == default || end < start)
            return [];

        var buckets = new List<DateTime>();
        for (var bucket = start; bucket <= end; bucket = bucket.AddMinutes(bucketMinutes))
            buckets.Add(bucket);

        return buckets;
    }

    /// <summary>
    /// Aggregates one metric into chart buckets and zeroes values while the app is not running.
    /// </summary>
    private static Dictionary<DateTime, double> BuildBucketMetric(
        IEnumerable<ActivitySnapshot> snapshots,
        DateTime? from,
        DateTime to,
        Func<ActivitySnapshot, double> selector,
        IReadOnlyDictionary<DateTime, bool> isAppRunningByBucket,
        int bucketMinutes
    )
    {
        var aggregated = snapshots
            .Where(s => (!from.HasValue || s.Minute >= from.Value) && s.Minute <= to)
            .GroupBy(s => ToBucketStart(s.Minute, bucketMinutes))
            .ToDictionary(g => g.Key, g => g.Sum(selector));

        var timeline = isAppRunningByBucket.Keys.OrderBy(t => t).ToList();
        var result = new Dictionary<DateTime, double>(timeline.Count);
        foreach (var bucket in timeline)
        {
            var isAppRunning = isAppRunningByBucket[bucket];
            if (!isAppRunning)
            {
                result[bucket] = 0;
                continue;
            }

            result[bucket] = aggregated.TryGetValue(bucket, out var value) ? value : 0;
        }

        return result;
    }

    /// <summary>
    /// Truncates a timestamp to the configured bucket boundary.
    /// </summary>
    private static DateTime ToBucketStart(DateTime minute, int bucketMinutes)
    {
        var normalizedMinute = (minute.Minute / bucketMinutes) * bucketMinutes;
        return new DateTime(minute.Year, minute.Month, minute.Day, minute.Hour, normalizedMinute, 0, minute.Kind);
    }

    /// <summary>
    /// Reconstructs app-running intervals from AppStarted/AppEnded lifecycle events.
    /// </summary>
    private static IReadOnlyList<(DateTime Start, DateTime End)> BuildAppRunningIntervals(
        IEnumerable<DeviceEvent> lifecycleEvents,
        DateTime chartEnd
    )
    {
        var intervals = new List<(DateTime Start, DateTime End)>();
        DateTime? openStart = null;

        // lifecycleEvents are expected to be prefiltered and ordered by DataService.GetDashboardEvents.
        foreach (var appEvent in lifecycleEvents)
        {
            if (appEvent.EventType == EventTypes.AppStarted)
            {
                openStart ??= appEvent.EventTime;
                continue;
            }

            if (openStart.HasValue && appEvent.EventTime > openStart.Value)
                intervals.Add((openStart.Value, appEvent.EventTime));

            openStart = null;
        }

        if (openStart.HasValue && chartEnd > openStart.Value)
            intervals.Add((openStart.Value, chartEnd));

        return intervals;
    }

    /// <summary>
    /// Returns true when the chart bucket intersects a running interval.
    /// </summary>
    private static bool BucketsOverlap(
        DateTime bucketStart,
        DateTime intervalStart,
        DateTime intervalEnd,
        int bucketMinutes
    )
    {
        var bucketEnd = bucketStart.AddMinutes(bucketMinutes);
        return bucketStart < intervalEnd && intervalStart < bucketEnd;
    }

    /// <summary>
    /// Applies trailing moving-average smoothing across consecutive running buckets.
    /// Smoothing never crosses app-off boundaries.
    /// </summary>
    private static Dictionary<DateTime, double> SmoothBuckets(
        IReadOnlyDictionary<DateTime, double> valuesByBucket,
        IReadOnlyList<DateTime> timeline,
        IReadOnlyDictionary<DateTime, bool> isAppRunningByBucket,
        int smoothingWindow
    )
    {
        if (smoothingWindow <= 1)
            return timeline.ToDictionary(
                bucket => bucket,
                bucket => valuesByBucket.TryGetValue(bucket, out var value) ? value : 0
            );

        var result = new Dictionary<DateTime, double>(timeline.Count);
        for (var i = 0; i < timeline.Count; i++)
        {
            var bucket = timeline[i];
            if (!isAppRunningByBucket[bucket])
            {
                result[bucket] = 0;
                continue;
            }

            var sum = 0.0;
            var count = 0;
            for (var j = i; j >= 0 && count < smoothingWindow; j--)
            {
                var candidate = timeline[j];
                if (!isAppRunningByBucket[candidate])
                    break;

                sum += valuesByBucket.TryGetValue(candidate, out var value) ? value : 0;
                count++;
            }

            result[bucket] = count > 0 ? sum / count : 0;
        }

        return result;
    }

    /// <summary>
    /// Returns the earliest non-default timestamp.
    /// </summary>
    private static DateTime MinNonDefault(DateTime a, DateTime b)
    {
        if (a == default)
            return b;
        if (b == default)
            return a;
        return a < b ? a : b;
    }
}
