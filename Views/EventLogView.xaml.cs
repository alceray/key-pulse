using System.Windows.Controls;
using KeyPulse.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace KeyPulse.Views;

public partial class EventLogView : UserControl
{
    public EventLogView()
    {
        InitializeComponent();
        DataContext = App.ServiceProvider.GetRequiredService<EventLogViewModel>();
    }
}
