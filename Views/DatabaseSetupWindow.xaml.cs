using System.Windows;
using KeyPulse.Models;
using KeyPulse.Services;
using Serilog;

namespace KeyPulse.Views;

public partial class DatabaseSetupWindow : Window
{
    private readonly AppSettingsService _settingsService;
    private readonly IDatabaseCredentialStore _credentialStore;
    private readonly bool _recoveryMode;
    private readonly DatabaseSwitchService? _switchService;
    private readonly DatabaseConnectionSettingsService _connectionSettings;

    private DatabaseConnectionRole SelectedConnectionRole =>
        RecoveryConnectionComboBox.SelectedItem is DatabaseConnectionRole role
            ? role
            : DatabaseConnectionRole.Destination;

    public DatabaseSetupWindow(
        string caption,
        AppSettingsService settingsService,
        IDatabaseCredentialStore credentialStore,
        bool recoveryMode = false,
        string? failureMessage = null,
        DatabaseSwitchService? switchService = null
    )
    {
        InitializeComponent();
        Title = caption;
        _settingsService = settingsService;
        _credentialStore = credentialStore;
        _connectionSettings = new(settingsService, credentialStore);
        _recoveryMode = recoveryMode;
        _switchService = switchService;
        SslModeComboBox.ItemsSource = Enum.GetValues<PostgreSqlSslMode>();

        var settings = settingsService.GetSettings();
        var direct =
            settings.DatabaseProvider == DatabaseProvider.PostgreSql
            && settings.PendingDatabaseProvider == DatabaseProvider.PostgreSql;
        RecoveryConnectionPanel.Visibility = recoveryMode ? Visibility.Visible : Visibility.Collapsed;
        RecoveryConnectionComboBox.ItemsSource = direct
            ? new[] { DatabaseConnectionRole.Source, DatabaseConnectionRole.Destination }
            : new[]
            {
                settings.PendingDatabaseProvider == DatabaseProvider.PostgreSql
                    ? DatabaseConnectionRole.Destination
                    : DatabaseConnectionRole.Source,
            };
        RecoveryConnectionComboBox.SelectedItem = direct
            ? switchService?.FailureConnection ?? DatabaseConnectionRole.Destination
            : ((DatabaseConnectionRole[])RecoveryConnectionComboBox.ItemsSource)[0];
        LoadRecoveryConnection();
        HostTextBox.IsReadOnly = PortTextBox.IsReadOnly = DatabaseTextBox.IsReadOnly = recoveryMode;
        SqliteRadio.IsEnabled = PostgreSqlRadio.IsEnabled = !settings.PendingDatabaseProvider.HasValue;
        if (direct)
            DescriptionText.Text =
                $"Source: {settings.PostgreSql.Describe()}\nDestination: {DatabaseConnectionSettingsService.ResolveRecoveryPostgreSql(settings, DatabaseConnectionRole.Destination).Connection.Describe()}. Correct authentication below, retry, or cancel.";

        if (settings.DatabaseProvider == DatabaseProvider.PostgreSql || recoveryMode)
            PostgreSqlRadio.IsChecked = true;

        if (recoveryMode)
        {
            HeadingText.Text = "Database startup needs attention";
            if (!direct)
                DescriptionText.Text =
                    "KeyPulse could not finish opening or moving its history. Retry after correcting the problem, or choose a recovery option below.";
            ContinueButton.Content = "Retry";
            CancelChangeButton.Visibility = settings.PendingDatabaseProvider.HasValue
                ? Visibility.Visible
                : Visibility.Collapsed;
            UseLocalButton.Visibility =
                settings.DatabaseProvider == DatabaseProvider.PostgreSql ? Visibility.Visible : Visibility.Collapsed;
            if (switchService == null)
                throw new ArgumentNullException(nameof(switchService));
        }

        if (!string.IsNullOrWhiteSpace(failureMessage))
            StatusText.Text = $"{switchService?.FailureConnection}: {failureMessage}";
    }

    private void OnRecoveryConnectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_settingsService != null)
            LoadRecoveryConnection();
    }

    private void LoadRecoveryConnection()
    {
        var settings = _settingsService.GetSettings();
        var (connection, reference) = DatabaseConnectionSettingsService.ResolveRecoveryPostgreSql(
            settings,
            SelectedConnectionRole
        );
        HostTextBox.Text = connection.Host;
        PortTextBox.Text = connection.Port.ToString();
        DatabaseTextBox.Text = connection.Database;
        UsernameTextBox.Text = connection.Username;
        SslModeComboBox.SelectedItem = connection.SslMode;
        try
        {
            PasswordInput.Password = _credentialStore.ReadPostgreSqlPassword(reference) ?? "";
        }
        catch (Exception ex)
        {
            PasswordInput.Password = "";
            StatusText.Text = ex.Message;
        }
    }

    private void OnProviderChanged(object sender, RoutedEventArgs e)
    {
        if (PostgreSqlPanel == null)
            return;
        PostgreSqlPanel.Visibility = PostgreSqlRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private PostgreSqlConnectionSettings ReadPostgreSqlSettings()
    {
        if (!int.TryParse(PortTextBox.Text, out var port))
            throw new InvalidOperationException("Enter a valid PostgreSQL port");

        return new PostgreSqlConnectionSettings
        {
            Host = HostTextBox.Text.Trim(),
            Port = port,
            Database = DatabaseTextBox.Text.Trim(),
            Username = UsernameTextBox.Text.Trim(),
            SslMode = SslModeComboBox.SelectedItem is PostgreSqlSslMode sslMode ? sslMode : PostgreSqlSslMode.Prefer,
        };
    }

    private string ReadPassword() =>
        string.IsNullOrEmpty(PasswordInput.Password)
            ? throw new InvalidOperationException("Enter the PostgreSQL password")
            : PasswordInput.Password;

    private async void OnTestConnectionClick(object sender, RoutedEventArgs e)
    {
        await RunPostgreSqlActionAsync(async () =>
        {
            if (
                _recoveryMode
                && _settingsService.GetSettings().PendingDatabaseProvider.HasValue
                && SelectedConnectionRole == DatabaseConnectionRole.Source
            )
                await DatabaseConfigurationService.TestPostgreSqlReadAsync(ReadPostgreSqlSettings(), ReadPassword());
            else
                await DatabaseConfigurationService.TestPostgreSqlAsync(ReadPostgreSqlSettings(), ReadPassword());
            StatusText.Text = "Connection successful.";
        });
    }

    private async void OnContinueClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true);
            var settings = _settingsService.GetSettings();

            if (_recoveryMode && settings.PendingDatabaseProvider.HasValue)
            {
                try
                {
                    if (await _switchService!.TryCompletePendingSwitchAsync())
                    {
                        DialogResult = true;
                        return;
                    }
                }
                catch when (SelectedConnectionRole == DatabaseConnectionRole.Destination)
                {
                    // Destination authentication may need repair before its marker can be read.
                }
                await _switchService!.UpdatePendingAuthenticationAsync(
                    SelectedConnectionRole,
                    ReadPostgreSqlSettings(),
                    ReadPassword()
                );
                DialogResult = true;
                return;
            }

            settings.IsFirstLaunch = false;
            if (SqliteRadio.IsChecked == true)
            {
                if (_recoveryMode && settings.DatabaseProvider == DatabaseProvider.PostgreSql)
                {
                    if (
                        MessageBox.Show(
                            "Copy PostgreSQL history to SQLite now? The current local database will be backed up and replaced with the copied history.",
                            Title,
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Information,
                            MessageBoxResult.No
                        ) != MessageBoxResult.Yes
                    )
                        return;
                    _connectionSettings.ScheduleSqlite(settings);
                }
                else
                {
                    settings.DatabaseProvider = DatabaseProvider.Sqlite;
                    settings.ClearPendingDatabaseSwitch();
                    _settingsService.SaveSettings(settings);
                }
            }
            else
            {
                var connection = ReadPostgreSqlSettings();
                var password = ReadPassword();
                await DatabaseConfigurationService.TestPostgreSqlAsync(connection, password);
                if (_recoveryMode && settings.DatabaseProvider == DatabaseProvider.PostgreSql)
                    _connectionSettings.UpdateAuthentication(settings, connection, password);
                else
                    _connectionSettings.SchedulePostgreSql(
                        settings,
                        connection,
                        password,
                        DatabaseConfigurationService.HasSqliteHistory(),
                        replaceExisting: false
                    );
            }

            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            Log.Warning(ex, "Database setup could not be completed");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RunPostgreSqlActionAsync(Func<Task> action)
    {
        try
        {
            SetBusy(true);
            StatusText.Text = "Checking database...";
            await action();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            Log.Debug(ex, "Database recovery or connection check failed");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        PostgreSqlPanel.IsEnabled = !busy;
        RecoveryConnectionComboBox.IsEnabled = !busy;
        TestButton.IsEnabled = !busy;
        ContinueButton.IsEnabled = !busy;
        CancelChangeButton.IsEnabled = !busy;
        UseLocalButton.IsEnabled = !busy;
    }

    private async void OnCancelChangeClick(object sender, RoutedEventArgs e) => await RunRecoveryActionAsync(false);

    private async void OnUseLocalClick(object sender, RoutedEventArgs e)
    {
        if (
            MessageBox.Show(
                "Use SQLite without copying PostgreSQL history? The local file may be older or empty. History stored only in PostgreSQL will remain there.",
                Title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No
            ) != MessageBoxResult.Yes
        )
            return;
        await RunRecoveryActionAsync(true);
    }

    private async Task RunRecoveryActionAsync(bool useLocalWithoutCopying)
    {
        await RunPostgreSqlActionAsync(async () =>
        {
            await _switchService!.CancelPendingSwitchAsync(useLocalWithoutCopying);
            DialogResult = true;
        });
    }
}
