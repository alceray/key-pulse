using System.ComponentModel;
using KeyPulse.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace KeyPulse.Views;

public partial class SettingsView
{
    public SettingsView()
    {
        InitializeComponent();
        var viewModel = App.ServiceProvider.GetRequiredService<SettingsViewModel>();
        DataContext = viewModel;
        PropertyChangedEventManager.AddHandler(
            viewModel,
            OnViewModelPasswordChanged,
            nameof(SettingsViewModel.PostgreSqlPassword)
        );
        PostgreSqlPasswordBox.Password = viewModel.PostgreSqlPassword;
    }

    private void OnViewModelPasswordChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            DataContext is SettingsViewModel viewModel
            && PostgreSqlPasswordBox.Password != viewModel.PostgreSqlPassword
        )
            PostgreSqlPasswordBox.Password = viewModel.PostgreSqlPassword;
    }

    private void OnPostgreSqlPasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel && sender is System.Windows.Controls.PasswordBox passwordBox)
            viewModel.PostgreSqlPassword = passwordBox.Password;
    }

    private void OnEditDatabaseConnectionClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
            return;
        viewModel.BeginEditConnection();
    }

    private async void OnCancelDatabaseChangesClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
            return;
        await viewModel.CancelDatabaseChangesAsync();
    }
}
