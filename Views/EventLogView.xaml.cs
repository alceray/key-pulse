using KeyPulse.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace KeyPulse.Views;

public partial class EventLogView
{
    public EventLogView()
    {
        InitializeComponent();
        DataContext = App.ServiceProvider.GetRequiredService<EventLogViewModel>();
    }
}
