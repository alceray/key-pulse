using System.Windows.Media;

namespace KeyPulse.Configuration;

public static class AppStyles
{
    public static Brush FatalBrush { get; } = MakeFrozen(0xE5, 0x39, 0x35);
    public static Brush ErrorBrush { get; } = MakeFrozen(0xFF, 0x57, 0x22);
    public static Brush WarningBrush { get; } = MakeFrozen(0xF5, 0xA6, 0x23);
    public static Brush InformationBrush { get; } = MakeFrozen(0x42, 0x9C, 0xF4);
    public static Brush DebugBrush { get; } = MakeFrozen(0x61, 0x61, 0x61);
    public static Brush DividerBrush { get; } = MakeFrozen(0x88, 0x88, 0x88);
    public static Brush BlackBrush { get; } = MakeFrozen(0x00, 0x00, 0x00);
    public static Brush SearchCounterBrush { get; } = MakeFrozen(0x88, 0x00, 0x00, 0x00);
    public static Brush LogBorderBrush { get; } = MakeFrozen(0x33, 0x00, 0x00, 0x00);
    public static Brush StatusBackgroundBrush { get; } = MakeFrozen(0xCC, 0x1B, 0x1B, 0x1B);

    public static Brush GetLogLevelBrush(string levelName)
    {
        return levelName switch
        {
            "Fatal" => FatalBrush,
            "Error" => ErrorBrush,
            "Warning" => WarningBrush,
            "Information" => InformationBrush,
            "Debug" => DebugBrush,
            _ => Brushes.Transparent,
        };
    }

    public static Brush GetLogTokenBrush(string token)
    {
        return token.ToUpperInvariant() switch
        {
            AppConstants.Troubleshooting.FatalToken => FatalBrush,
            AppConstants.Troubleshooting.ErrorToken => ErrorBrush,
            AppConstants.Troubleshooting.WarningToken => WarningBrush,
            AppConstants.Troubleshooting.InformationToken => InformationBrush,
            AppConstants.Troubleshooting.DebugToken => DebugBrush,
            _ => BlackBrush,
        };
    }

    private static Brush MakeFrozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Brush MakeFrozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
