using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using KeyPulse.Helpers;
using KeyPulse.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace KeyPulse.Views;

public partial class DeviceListView : UserControl
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
        throw new NotImplementedException();
    }
}
