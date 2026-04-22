namespace KeyPulse.Helpers;

/// <summary>
/// Formats DateTime and TimeSpan values as human-readable strings.
/// </summary>
public static class TimeFormatter
{
    /// <summary>
    /// Converts a DateTime to a relative time string (e.g., "2 hours ago", "5 seconds ago").
    /// </summary>
    public static string ToRelativeTime(DateTime dateTime)
    {
        var timeSpan = DateTime.Now - dateTime;

        if (timeSpan.TotalSeconds < 60)
            return $"{(int)timeSpan.TotalSeconds} seconds ago";

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
    /// Converts a TimeSpan to a compact string showing the top 3 most significant units.
    /// Supported units: y, mo, w, d, h, m, s.
    /// </summary>
    public static string FormatDuration(TimeSpan timeSpan)
    {
        if (timeSpan <= TimeSpan.Zero)
            return "0s";

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
        if (seconds > 0)
            parts.Add($"{seconds}s");

        return string.Join(" ", parts.Take(3));
    }
}
