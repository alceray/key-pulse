using System.Windows.Media;
using KeyPulse.Configuration;
using OxyPlot;
using OxyPlot.Series;

namespace KeyPulse.ViewModels.Dashboard;

internal static class DashboardPlotTheme
{
    // Apply on the UI thread after each data/selection rebuild, when series still have their base colors.
    // This also handles a theme switch that occurred while a background refresh was in flight.
    public static void Apply(PlotModel model)
    {
        static OxyColor ToOxy(Brush brush)
        {
            var color = ((SolidColorBrush)brush).Color;
            return OxyColor.FromArgb(color.A, color.R, color.G, color.B);
        }

        model.Background = ToOxy(AppColorPalette.WindowBackgroundBrush);
        model.PlotAreaBackground = model.Background;
        model.TextColor = ToOxy(AppColorPalette.PrimaryTextBrush);
        model.TitleColor = model.TextColor;
        model.SubtitleColor = ToOxy(AppColorPalette.SecondaryTextBrush);
        model.PlotAreaBorderColor = ToOxy(AppColorPalette.BorderBrush);
        foreach (var axis in model.Axes)
        {
            axis.TextColor = model.TextColor;
            axis.TitleColor = model.TextColor;
            axis.AxislineColor = model.PlotAreaBorderColor;
            axis.TicklineColor = model.PlotAreaBorderColor;
            axis.MajorGridlineColor = model.PlotAreaBorderColor;
            axis.MinorGridlineColor = model.PlotAreaBorderColor;
        }
        foreach (var series in model.Series)
        {
            if (series is LineSeries line)
                line.Color = ForTheme(line.Color, AppColorPalette.IsDark);
            else if (series is PieSeries pie)
            {
                pie.TextColor = model.TextColor;
                // Slice fills are lifted in dark mode, so inner labels need dark ink.
                pie.InsideLabelColor = OxyColors.Black;
                pie.Stroke = model.Background;
                foreach (var slice in pie.Slices)
                    slice.Fill = ForTheme(slice.Fill, AppColorPalette.IsDark);
            }
        }
        model.InvalidatePlot(false);
    }

    // Lift darker device colors while preserving hue, rank assignment, and selection alpha.
    internal static OxyColor ForTheme(OxyColor color, bool darkMode)
    {
        if (!darkMode || color == OxyColors.Automatic || color == OxyColors.Undefined)
            return color;
        static byte Lift(byte channel) => (byte)(channel + (255 - channel) * 0.25);
        return OxyColor.FromArgb(color.A, Lift(color.R), Lift(color.G), Lift(color.B));
    }
}
