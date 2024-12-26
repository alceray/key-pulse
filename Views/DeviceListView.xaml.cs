using KeyPulse.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Data;


namespace KeyPulse.Views
{
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
            if (value is TimeSpan timeSpan)
            {
                var parts = new List<string>();
                if (timeSpan.Days > 0)
                    parts.Add($"{timeSpan.Days}d");
                if (timeSpan.Hours > 0)
                    parts.Add($"{timeSpan.Hours:D2}h");
                if (timeSpan.Minutes > 0)
                    parts.Add($"{timeSpan.Minutes:D2}m");
                parts.Add($"{timeSpan.Seconds:D2}s");
                return string.Join( " ", parts);
            }
            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
