namespace KeyPulse.ViewModels.Settings;

/// <summary>A user-selectable activity-retention choice; Months of 0 means keep everything forever.</summary>
public sealed record RetentionOption(string Label, int Months);

/// <summary>The fixed retention choices offered in Settings, plus mapping from persisted values.</summary>
public static class RetentionOptions
{
    public static readonly IReadOnlyList<RetentionOption> All =
    [
        new("6 months", 6),
        new("1 year", 12),
        new("2 years", 24),
        new("Forever", 0),
    ];

    /// <summary>Maps a persisted month count to its option; unknown values fall back to Forever.</summary>
    public static RetentionOption FromMonths(int months) =>
        All.FirstOrDefault(o => o.Months == months) ?? All.First(o => o.Months == 0);
}
