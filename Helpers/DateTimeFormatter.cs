namespace KeyPulse.Helpers;

/// <summary>
/// Formats DateTime values as human-readable relative time strings.
/// </summary>
public static class DateTimeFormatter
{
    /// <summary>
    /// Converts a DateTime to a relative time string (e.g., "2 hours ago", "5 seconds ago").
    /// </summary>
    public static string ToRelativeTime(DateTime dateTime)
    {
        var now = DateTime.UtcNow;
        var utcDateTime = dateTime.Kind == DateTimeKind.Utc ? dateTime : dateTime.ToUniversalTime();
        var timeSpan = now - utcDateTime;

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
}