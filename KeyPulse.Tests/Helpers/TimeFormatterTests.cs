using KeyPulse.Helpers;

namespace KeyPulse.Tests.Helpers;

/// <summary>
/// Coverage for the pure time helpers. Timezone-sensitive results are asserted against the framework
/// conversion (or use Local-kind inputs that pass through unchanged), so they hold on any machine.
/// Clock-dependent ToRelativeTime cases use mid-tier offsets to avoid boundary races.
/// </summary>
public class TimeFormatterTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void Truncation_PreservesDaylightSavingOccurrence(int utcHour)
    {
        var utc = new DateTime(2025, 11, 2, utcHour, 30, 45, DateTimeKind.Utc).AddTicks(1234567);
        utc.ToLocalTime().TruncateToSecond().ToUniversalTime().ShouldBe(utc.AddTicks(-1234567));
        utc.ToLocalTime()
            .TruncateToMinute()
            .ToUniversalTime()
            .ShouldBe(new DateTime(2025, 11, 2, utcHour, 30, 0, DateTimeKind.Utc));
    }

    // ── TruncateToMinute: zeroes sub-minute components, PRESERVES DateTimeKind ──

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void TruncateToMinute_ZeroesSecondsAndBelow_AndPreservesKind(DateTimeKind kind)
    {
        var input = new DateTime(2026, 5, 27, 14, 35, 47, 123, kind).AddTicks(456);

        var result = input.TruncateToMinute();

        result.ShouldBe(new DateTime(2026, 5, 27, 14, 35, 0, kind));
        result.Second.ShouldBe(0);
        result.Millisecond.ShouldBe(0);
        result.Kind.ShouldBe(kind); // key contract: Kind is carried through
    }

    // ── TruncateToSecond: zeroes sub-second components, PRESERVES DateTimeKind ──

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void TruncateToSecond_ZeroesMillisecondsAndBelow_AndPreservesKind(DateTimeKind kind)
    {
        var input = new DateTime(2026, 5, 27, 14, 35, 47, 123, kind).AddTicks(456);

        var result = input.TruncateToSecond();

        result.ShouldBe(new DateTime(2026, 5, 27, 14, 35, 47, kind));
        result.Second.ShouldBe(47); // seconds retained, unlike TruncateToMinute
        result.Millisecond.ShouldBe(0);
        result.Kind.ShouldBe(kind);
    }

    // ── NormalizeUtcMinute: zeroes sub-minute components, ALWAYS returns Utc ─────

    [Fact]
    public void NormalizeUtcMinute_OnUtcInput_TruncatesToMinute_AndStaysUtc()
    {
        var input = new DateTime(2026, 5, 27, 14, 35, 47, 123, DateTimeKind.Utc);

        var result = TimeFormatter.NormalizeUtcMinute(input);

        result.ShouldBe(new DateTime(2026, 5, 27, 14, 35, 0, DateTimeKind.Utc));
        result.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Fact]
    public void NormalizeUtcMinute_OnLocalInput_ConvertsToUtc()
    {
        var local = new DateTime(2026, 5, 27, 14, 35, 47, DateTimeKind.Local);

        var result = TimeFormatter.NormalizeUtcMinute(local);

        var expected = local.ToUniversalTime();
        result.ShouldBe(
            new DateTime(
                expected.Year,
                expected.Month,
                expected.Day,
                expected.Hour,
                expected.Minute,
                0,
                DateTimeKind.Utc
            )
        );
        result.Kind.ShouldBe(DateTimeKind.Utc);
    }

    /// <summary>
    /// The whole reason both helpers exist: given the same Local-kind instant, NormalizeUtcMinute
    /// forces Utc while TruncateToMinute keeps Local. They are NOT interchangeable (see CLAUDE.md).
    /// </summary>
    [Fact]
    public void NormalizeUtcMinute_And_TruncateToMinute_DifferOnKind()
    {
        var local = new DateTime(2026, 5, 27, 14, 35, 47, DateTimeKind.Local);

        TimeFormatter.NormalizeUtcMinute(local).Kind.ShouldBe(DateTimeKind.Utc);
        local.TruncateToMinute().Kind.ShouldBe(DateTimeKind.Local);
    }

    // ── ToLocalTime: 3 Kind branches (asserted against the framework) ──────────

    [Fact]
    public void ToLocalTime_LocalKind_ReturnedUnchanged()
    {
        var local = new DateTime(2026, 5, 20, 9, 0, 0, DateTimeKind.Local);
        TimeFormatter.ToLocalTime(local).ShouldBe(local);
    }

    [Fact]
    public void ToLocalTime_UtcKind_ConvertsToLocal()
    {
        var utc = new DateTime(2026, 5, 20, 9, 0, 0, DateTimeKind.Utc);
        var result = TimeFormatter.ToLocalTime(utc);
        result.ShouldBe(utc.ToLocalTime());
        result.Kind.ShouldBe(DateTimeKind.Local);
    }

    [Fact]
    public void ToLocalTime_UnspecifiedKind_TreatedAsUtc()
    {
        var unspecified = new DateTime(2026, 5, 20, 9, 0, 0, DateTimeKind.Unspecified);
        TimeFormatter
            .ToLocalTime(unspecified)
            .ShouldBe(DateTime.SpecifyKind(unspecified, DateTimeKind.Utc).ToLocalTime());
    }

    // ── ToLocalDay ──────────────────────────────────────────────────────────────

    [Fact]
    public void ToLocalDay_OnLocalInput_ReturnsThatCalendarDay()
    {
        var local = new DateTime(2026, 5, 27, 23, 59, 0, DateTimeKind.Local);
        TimeFormatter.ToLocalDay(local).ShouldBe(new DateOnly(2026, 5, 27));
    }

    // ── LocalDayToUtc ───────────────────────────────────────────────────────────

    [Fact]
    public void LocalDayToUtc_ReturnsUtcStartOfLocalDay()
    {
        var day = new DateOnly(2026, 5, 20);
        var result = TimeFormatter.LocalDayToUtc(day);
        result.ShouldBe(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime());
        result.Kind.ShouldBe(DateTimeKind.Utc);
    }

    // ── ToRelativeTime: every tier + singular/plural ───────────────────────────

    [Fact]
    public void ToRelativeTime_Seconds() =>
        TimeFormatter.ToRelativeTime(DateTime.Now.AddSeconds(-5)).ShouldBe("5 seconds ago");

    [Fact]
    public void ToRelativeTime_Minutes_Plural() =>
        TimeFormatter.ToRelativeTime(DateTime.Now.AddMinutes(-5)).ShouldBe("5 minutes ago");

    [Fact]
    public void ToRelativeTime_Minute_Singular() =>
        TimeFormatter.ToRelativeTime(DateTime.Now.AddSeconds(-61)).ShouldBe("1 minute ago");

    [Fact]
    public void ToRelativeTime_Hours_Plural() =>
        TimeFormatter.ToRelativeTime(DateTime.Now.AddHours(-5)).ShouldBe("5 hours ago");

    [Fact]
    public void ToRelativeTime_Hour_Singular() =>
        TimeFormatter.ToRelativeTime(DateTime.Now.AddMinutes(-61)).ShouldBe("1 hour ago");

    [Fact]
    public void ToRelativeTime_Days_Plural() =>
        TimeFormatter.ToRelativeTime(DateTime.Now.AddDays(-3)).ShouldBe("3 days ago");

    [Fact]
    public void ToRelativeTime_Day_Singular() =>
        TimeFormatter.ToRelativeTime(DateTime.Now.AddHours(-25)).ShouldBe("1 day ago");

    [Fact]
    public void ToRelativeTime_Weeks_Plural() =>
        TimeFormatter.ToRelativeTime(DateTime.Now.AddDays(-15)).ShouldBe("2 weeks ago");

    [Fact]
    public void ToRelativeTime_Week_Singular() =>
        TimeFormatter.ToRelativeTime(DateTime.Now.AddDays(-8)).ShouldBe("1 week ago");

    [Fact]
    public void ToRelativeTime_Months_Plural() =>
        TimeFormatter.ToRelativeTime(DateTime.Now.AddDays(-60)).ShouldBe("2 months ago");

    [Fact]
    public void ToRelativeTime_Month_Singular() =>
        TimeFormatter.ToRelativeTime(DateTime.Now.AddDays(-31)).ShouldBe("1 month ago");

    [Fact]
    public void ToRelativeTime_Years_Plural() =>
        TimeFormatter.ToRelativeTime(DateTime.Now.AddDays(-800)).ShouldBe("2 years ago");

    [Fact]
    public void ToRelativeTime_Year_Singular() =>
        TimeFormatter.ToRelativeTime(DateTime.Now.AddDays(-400)).ShouldBe("1 year ago");

    [Fact]
    public void ToRelativeTime_OneSecond_IsSingular() =>
        TimeFormatter.ToRelativeTime(DateTime.Now.AddSeconds(-1)).ShouldBe("1 second ago");

    [Fact]
    public void ToRelativeTime_FutureTimestamp_ReturnsJustNow() =>
        TimeFormatter.ToRelativeTime(DateTime.Now.AddHours(1)).ShouldBe("just now");

    // ── FormatDuration: top-3 units, no negative/zero, week & month units ──────

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(-5, "0s")]
    [InlineData(1, "1s")]
    [InlineData(90, "1m 30s")]
    [InlineData(3600, "1h")]
    [InlineData(86400 * 2 + 3600 * 3 + 60 * 4 + 5, "2d 3h 4m")] // capped at top 3 units
    [InlineData(86400 * 366, "1y 1d")] // 366 days => 1 year + 1 day
    [InlineData(7L * 86400, "1w")]
    [InlineData(10L * 86400, "1w 3d")]
    [InlineData(30L * 86400, "1mo")]
    [InlineData(35L * 86400, "1mo 5d")]
    [InlineData(365L * 86400 + 35L * 86400, "1y 1mo 5d")]
    [InlineData(403L * 86400, "1y 1mo 1w")] // y+mo+w+d collapses to top 3 (drops the trailing day)
    public void FormatDuration_FormatsTopThreeUnits(long totalSeconds, string expected) =>
        TimeFormatter.FormatDuration(TimeSpan.FromSeconds(totalSeconds)).ShouldBe(expected);

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(-5, "0m")]
    [InlineData(47, "0m")] // sub-minute floors to 0m, not 0s
    [InlineData(90, "1m")] // 1m 30s drops the seconds
    [InlineData(3600, "1h")]
    [InlineData(3661, "1h 1m")] // 1h 1m 1s drops the seconds
    [InlineData(86400 * 2 + 3600 * 3 + 60 * 4 + 5, "2d 3h 4m")]
    public void FormatDuration_WithoutSeconds_FloorsToMinute(long totalSeconds, string expected) =>
        TimeFormatter.FormatDuration(TimeSpan.FromSeconds(totalSeconds), includeSeconds: false).ShouldBe(expected);

    [Theory]
    [InlineData(86400 * 2 + 3600 * 3 + 60 * 4 + 5, "2d 3h 4m 5s")] // not capped at 3 units
    [InlineData(403L * 86400, "1y 1mo 1w 1d")] // keeps the trailing day the top-3 form drops
    public void FormatDuration_WithoutUnitCap_ShowsEveryUnit(long totalSeconds, string expected) =>
        TimeFormatter.FormatDuration(TimeSpan.FromSeconds(totalSeconds), maxUnits: int.MaxValue).ShouldBe(expected);

    // ── FormatDateRange ─────────────────────────────────────────────────────────

    [Fact]
    public void FormatDateRange_WithNullFrom_ReturnsAllTime() =>
        TimeFormatter
            .FormatDateRange(null, new DateTime(2026, 5, 27, 0, 0, 0, DateTimeKind.Local))
            .ShouldBe("All Time");

    [Fact]
    public void FormatDateRange_SameDay_CollapsesToSingleDate()
    {
        var from = new DateTime(2026, 5, 27, 8, 0, 0, DateTimeKind.Local);
        var to = new DateTime(2026, 5, 27, 20, 0, 0, DateTimeKind.Local);

        TimeFormatter.FormatDateRange(from, to).ShouldBe("May 27, 2026");
    }

    [Fact]
    public void FormatDateRange_DifferentDays_JoinsWithDash()
    {
        var from = new DateTime(2026, 5, 20, 8, 0, 0, DateTimeKind.Local);
        var to = new DateTime(2026, 5, 21, 8, 0, 0, DateTimeKind.Local);

        TimeFormatter.FormatDateRange(from, to).ShouldBe($"{from:MMM dd, yyyy} - {to:MMM dd, yyyy}");
    }
}
