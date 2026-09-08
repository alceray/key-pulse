using Serilog;

namespace KeyPulse.Helpers;

/// <summary>
/// Formats DateTime and TimeSpan values as human-readable strings.
/// </summary>
public static class TimeFormatter
{
    /// <summary>
    /// Formats a date range as "May 01, 2026 - May 02, 2026" or "All Time".
    /// </summary>
    public static string FormatDateRange(DateTime? from, DateTime to)
    {
        if (!from.HasValue)
            return "All Time";
        var fromLocal = ToLocalTime(from.Value);
        var toLocal = ToLocalTime(to);
        string fromStr = fromLocal.ToString("MMM dd, yyyy");
        string toStr = toLocal.ToString("MMM dd, yyyy");
        if (fromStr == toStr)
            return fromStr;
        return $"{fromStr} - {toStr}";
    }

    /// <summary>
    /// Converts a persisted UTC timestamp to the local calendar day it falls on.
    /// </summary>
    public static DateOnly ToLocalDay(DateTime dateTime) => DateOnly.FromDateTime(ToLocalTime(dateTime));

    /// <summary>
    /// Converts a persisted timestamp to local time for display/use.
    /// Treats unspecified values as UTC to match legacy SQLite reads.
    /// </summary>
    public static DateTime ToLocalTime(DateTime dateTime)
    {
        return dateTime.Kind switch
        {
            DateTimeKind.Local => dateTime,
            DateTimeKind.Utc => dateTime.ToLocalTime(),
            _ => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc).ToLocalTime(),
        };
    }

    /// <summary>
    /// Converts a DateTime to a relative time string (e.g., "2 hours ago", "5 seconds ago").
    /// </summary>
    public static string ToRelativeTime(DateTime dateTime)
    {
        var localDateTime = ToLocalTime(dateTime);
        var timeSpan = DateTime.Now - localDateTime;

        // A future timestamp means a clock-skew or logic bug upstream; surface it rather than render
        // a nonsensical negative duration.
        if (timeSpan < TimeSpan.Zero)
        {
            Log.Error("Future timestamp received: {Timestamp:o}", dateTime);
            return "just now";
        }

        if (timeSpan.TotalSeconds < 60)
        {
            var seconds = (int)timeSpan.TotalSeconds;
            return $"{seconds} {(seconds == 1 ? "second" : "seconds")} ago";
        }

        if (timeSpan.TotalMinutes < 60)
        {
            var mins = (int)timeSpan.TotalMinutes;
            return $"{mins} {(mins == 1 ? "minute" : "minutes")} ago";
        }

        if (timeSpan.TotalHours < 24)
        {
            var hours = (int)timeSpan.TotalHours;
            return $"{hours} {(hours == 1 ? "hour" : "hours")} ago";
        }

        if (timeSpan.TotalDays < 7)
        {
            var days = (int)timeSpan.TotalDays;
            return $"{days} {(days == 1 ? "day" : "days")} ago";
        }

        if (timeSpan.TotalDays < 30)
        {
            var weeks = (int)(timeSpan.TotalDays / 7);
            return $"{weeks} {(weeks == 1 ? "week" : "weeks")} ago";
        }

        if (timeSpan.TotalDays < 365)
        {
            var months = (int)(timeSpan.TotalDays / 30);
            return $"{months} {(months == 1 ? "month" : "months")} ago";
        }

        var years = (int)(timeSpan.TotalDays / 365);
        return $"{years} {(years == 1 ? "year" : "years")} ago";
    }

    /// <summary>
    /// Converts a TimeSpan to a compact string showing the top <paramref name="maxUnits"/> most
    /// significant units (3 by default). Supported units: y, mo, w, d, h, m, s. Pass
    /// <paramref name="includeSeconds"/> false to floor to the minute, used for settled values that no
    /// longer tick; pass a larger <paramref name="maxUnits"/> to show the full breakdown.
    /// </summary>
    public static string FormatDuration(TimeSpan timeSpan, bool includeSeconds = true, int maxUnits = 3)
    {
        if (timeSpan <= TimeSpan.Zero)
            return includeSeconds ? "0s" : "0m";

        var t = (long)timeSpan.TotalSeconds;
        var years = t / (365 * 24 * 3600);
        t %= 365 * 24 * 3600;
        var months = t / (30 * 24 * 3600);
        t %= 30 * 24 * 3600;
        var weeks = t / (7 * 24 * 3600);
        t %= 7 * 24 * 3600;
        var days = t / (24 * 3600);
        t %= 24 * 3600;
        var hours = t / 3600;
        t %= 3600;
        var minutes = t / 60;
        var seconds = t % 60;

        var parts = new List<string>();
        if (years > 0)
            parts.Add($"{years}y");
        if (months > 0)
            parts.Add($"{months}mo");
        if (weeks > 0)
            parts.Add($"{weeks}w");
        if (days > 0)
            parts.Add($"{days}d");
        if (hours > 0)
            parts.Add($"{hours}h");
        if (minutes > 0)
            parts.Add($"{minutes}m");
        if (includeSeconds && seconds > 0)
            parts.Add($"{seconds}s");

        // A sub-minute span has nothing left once seconds are dropped; floor it to "0m".
        if (parts.Count == 0)
            return includeSeconds ? "0s" : "0m";

        return string.Join(" ", parts.Take(maxUnits));
    }

    /// <summary>
    /// Truncates a DateTime to the start of its minute (zeroes out seconds, milliseconds, ticks).
    /// Preserves DateTimeKind.
    /// </summary>
    public static DateTime TruncateToMinute(this DateTime dt)
    {
        return dt.AddTicks(-(dt.Ticks % TimeSpan.TicksPerMinute));
    }

    /// <summary>
    /// Truncates a DateTime to the start of its second (zeroes out milliseconds and ticks).
    /// Preserves DateTimeKind. Used by the EF value converters to keep persisted timestamps
    /// at second resolution.
    /// </summary>
    public static DateTime TruncateToSecond(this DateTime dt)
    {
        // Reconstructing a local clock time loses which occurrence of a repeated daylight-saving hour it represents.
        return dt.AddTicks(-(dt.Ticks % TimeSpan.TicksPerSecond));
    }

    /// <summary>
    /// Normalizes a timestamp to its UTC minute boundary.
    /// Non-UTC values are converted to UTC before truncation.
    /// </summary>
    public static DateTime NormalizeUtcMinute(DateTime timestamp)
    {
        return timestamp.ToUniversalTime().TruncateToMinute();
    }

    /// <summary>
    /// Converts a local calendar day to its UTC start boundary.
    /// Useful for inclusive day-range queries represented as UTC timestamps.
    /// </summary>
    public static DateTime LocalDayToUtc(DateOnly day)
    {
        var localStart = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
        return localStart.ToUniversalTime();
    }
}
