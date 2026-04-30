using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using KeyPulse.Helpers;
using KeyPulse.Models;
using KeyPulse.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace KeyPulse.Views;

public partial class DeviceListView
{
    public DeviceListView()
    {
        InitializeComponent();
        DataContext = App.ServiceProvider.GetRequiredService<DeviceListViewModel>();
    }
}

public class TimeSpanToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is TimeSpan ts ? TimeFormatter.FormatDuration(ts) : string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts activity states to colors:
/// - Active: blue (#0066CC)
/// - Otherwise: black (default)
/// </summary>
public class ActivityColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool isActive)
            return new SolidColorBrush(Colors.Black);

        return isActive
            ? new SolidColorBrush(Color.FromRgb(0, 102, 204)) // Blue
            : new SolidColorBrush(Colors.Black); // Black (default)
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts a device type to visibility for icon display.
/// Shows the icon only if the device type matches the converter parameter.
/// </summary>
public class DeviceTypeToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DeviceTypes deviceType || parameter is not string typeStr)
            return Visibility.Collapsed;

        var targetTypeEnum = Enum.TryParse<DeviceTypes>(typeStr, out var result) ? result : DeviceTypes.Unknown;
        return deviceType == targetTypeEnum ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
