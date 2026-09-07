using System.Windows;
using System.Windows.Media;

namespace KeyPulse.Configuration;

// Frozen brushes are safe for code consumers. XAML uses DynamicResource with these property names.
// Device-series hues are intentionally owned by DashboardDeviceColorPalette.
public static class AppColorPalette
{
    private static readonly IReadOnlyDictionary<string, Brush> Light = CreatePalette(false);
    private static readonly IReadOnlyDictionary<string, Brush> Dark = CreatePalette(true);
    private static IReadOnlyDictionary<string, Brush> _current = Light;
    public static bool IsDark => ReferenceEquals(_current, Dark);

    public static Brush WindowBackgroundBrush => _current[nameof(WindowBackgroundBrush)];
    public static Brush PrimaryTextBrush => _current[nameof(PrimaryTextBrush)];
    public static Brush SecondaryTextBrush => _current[nameof(SecondaryTextBrush)];
    public static Brush MutedBrush => _current[nameof(MutedBrush)];
    public static Brush SurfaceBrush => _current[nameof(SurfaceBrush)];
    public static Brush HoverBrush => _current[nameof(HoverBrush)];
    public static Brush BorderBrush => _current[nameof(BorderBrush)];
    public static Brush FatalBrush => _current[nameof(FatalBrush)];
    public static Brush ErrorBrush => _current[nameof(ErrorBrush)];
    public static Brush WarningBrush => _current[nameof(WarningBrush)];
    public static Brush InformationBrush => _current[nameof(InformationBrush)];
    public static Brush ConnectedBrush => _current[nameof(ConnectedBrush)];
    public static Brush DisconnectedBrush => _current[nameof(DisconnectedBrush)];
    public static Brush HiddenBrush => _current[nameof(HiddenBrush)];
    public static Brush ActiveBrush => _current[nameof(ActiveBrush)];
    public static Brush ToastBackgroundBrush => _current[nameof(ToastBackgroundBrush)];
    public static Brush CalendarTileBackgroundBrush => _current[nameof(CalendarTileBackgroundBrush)];
    public static Brush CalendarSelectedTileBackgroundBrush => _current[nameof(CalendarSelectedTileBackgroundBrush)];
    public static Brush SearchHighlightBrush => _current[nameof(SearchHighlightBrush)];
    public static Brush SearchHighlightActiveBrush => _current[nameof(SearchHighlightActiveBrush)];

    // Tray glyphs belong to the Windows tray rather than the application theme.
    public static Color PauseIconColor { get; } = Color.FromRgb(0x33, 0x33, 0x33);

    internal static ResourceDictionary CreateResources(bool darkMode)
    {
        var resources = new ResourceDictionary();
        foreach (var (key, brush) in darkMode ? Dark : Light)
            resources[key] = brush;
        // Default WPF templates also use system resource keys (for example the scrollbar corner).
        resources[SystemColors.WindowBrushKey] = resources["WindowBackgroundBrush"];
        resources[SystemColors.WindowTextBrushKey] = resources["PrimaryTextBrush"];
        resources[SystemColors.ControlBrushKey] = resources["SurfaceBrush"];
        resources[SystemColors.ControlTextBrushKey] = resources["PrimaryTextBrush"];
        resources[SystemColors.GrayTextBrushKey] = resources["DisabledTextBrush"];
        resources[SystemColors.HighlightBrushKey] = resources["SelectionBrush"];
        resources[SystemColors.HighlightTextBrushKey] = resources["PrimaryTextBrush"];
        resources[SystemColors.InactiveSelectionHighlightBrushKey] = resources["SelectionBrush"];
        resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = resources["PrimaryTextBrush"];
        resources["IsDarkTheme"] = darkMode;
        return resources;
    }

    internal static void Apply(ResourceDictionary resources, bool darkMode)
    {
        _current = darkMode ? Dark : Light;
        var theme = CreateResources(darkMode);
        foreach (var key in theme.Keys)
            resources[key] = theme[key];
    }

    private static IReadOnlyDictionary<string, Brush> CreatePalette(bool darkMode)
    {
        Brush MakeBrush(string light, string dark)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(darkMode ? dark : light));
            brush.Freeze();
            return brush;
        }

        return new Dictionary<string, Brush>
        {
            ["WindowBackgroundBrush"] = MakeBrush("#FFFFFF", "#181B20"),
            ["PrimaryTextBrush"] = MakeBrush("#000000", "#E8EAED"),
            ["SecondaryTextBrush"] = MakeBrush("#696969", "#B8BEC8"),
            ["MutedBrush"] = MakeBrush("#707070", "#A6AFBC"),
            ["SurfaceBrush"] = MakeBrush("#F5F5F5", "#242830"),
            ["HoverBrush"] = MakeBrush("#E5E7EB", "#343C49"),
            ["BorderBrush"] = MakeBrush("#DCDCDC", "#454D5A"),
            ["SelectionBrush"] = MakeBrush("#DCE9FA", "#304B6D"),
            ["DisabledTextBrush"] = MakeBrush("#767676", "#9098A5"),
            ["AccentBrush"] = MakeBrush("#4169E1", "#8BB4FF"),
            ["AccentTextBrush"] = MakeBrush("#FFFFFF", "#181B20"),
            ["FatalBrush"] = MakeBrush("#DC143C", "#FF829B"),
            ["ErrorBrush"] = MakeBrush("#C13C15", "#FF987B"),
            ["WarningBrush"] = MakeBrush("#936800", "#E6C36A"),
            ["InformationBrush"] = MakeBrush("#00838F", "#6AD3DB"),
            ["ConnectedBrush"] = MakeBrush("#4169E1", "#8BB4FF"),
            ["DisconnectedBrush"] = MakeBrush("#B22222", "#F59090"),
            ["HiddenBrush"] = MakeBrush("#696969", "#A6AFBC"),
            ["ActiveBrush"] = MakeBrush("#2E8B57", "#75D6A1"),
            ["ToastBackgroundBrush"] = MakeBrush("#ED1B1B1B", "#F0343C49"),
            ["CalendarTileBackgroundBrush"] = MakeBrush("#FDF5E6", "#343024"),
            ["CalendarSelectedTileBackgroundBrush"] = MakeBrush("#F5DEB3", "#5B492A"),
            ["SearchHighlightBrush"] = MakeBrush("#FFFF00", "#E6C36A"),
            ["SearchHighlightActiveBrush"] = MakeBrush("#FFA500", "#FFAC66"),
            ["SearchHighlightTextBrush"] = MakeBrush("#000000", "#000000"),
            ["SqliteLogoBrush"] = MakeBrush("#003B57", "#82C8E8"),
            ["PostgreSqlLogoBrush"] = MakeBrush("#336791", "#8DBCE3"),
        };
    }

    public static string GetLogLevelResourceKey(string levelName) =>
        levelName switch
        {
            "Fatal" => nameof(FatalBrush),
            "Error" => nameof(ErrorBrush),
            "Warning" => nameof(WarningBrush),
            "Information" => nameof(InformationBrush),
            _ => nameof(MutedBrush),
        };

    public static Brush GetLogLevelBrush(string levelName) => _current[GetLogLevelResourceKey(levelName)];

    public static string GetLogTokenResourceKey(string token) =>
        token.ToUpperInvariant() switch
        {
            AppConstants.Troubleshooting.FatalToken => nameof(FatalBrush),
            AppConstants.Troubleshooting.ErrorToken => nameof(ErrorBrush),
            AppConstants.Troubleshooting.WarningToken => nameof(WarningBrush),
            AppConstants.Troubleshooting.InformationToken => nameof(InformationBrush),
            AppConstants.Troubleshooting.DebugToken => nameof(MutedBrush),
            _ => nameof(PrimaryTextBrush),
        };

    public static Brush GetLogTokenBrush(string token) => _current[GetLogTokenResourceKey(token)];
}

/// <summary>Light defaults available during XAML initialization and in the designer.</summary>
public sealed class ThemeResources : ResourceDictionary
{
    public ThemeResources() => MergedDictionaries.Add(AppColorPalette.CreateResources(false));
}
