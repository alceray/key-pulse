using System.Windows.Controls;
using KeyPulse.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace KeyPulse.Views;

public partial class DashboardView : UserControl
{
    private readonly DashboardViewModel _viewModel;

    public DashboardView()
    {
        InitializeComponent();
        _viewModel = App.ServiceProvider.GetRequiredService<DashboardViewModel>();
        DataContext = _viewModel;
        Unloaded += DashboardView_Unloaded;
    }

    private void DashboardView_Unloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        Unloaded -= DashboardView_Unloaded;
        _viewModel.Dispose();
    }
}
