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
        _recoveryMode = recoveryMode;
        _switchService = switchService;
        SslModeComboBox.ItemsSource = Enum.GetValues<PostgreSqlSslMode>();

        var settings = settingsService.GetSettings();
        var postgreSql = settings.PostgreSql;
        HostTextBox.Text = postgreSql.Host;
        PortTextBox.Text = postgreSql.Port.ToString();
        DatabaseTextBox.Text = postgreSql.Database;
        UsernameTextBox.Text = postgreSql.Username;
        SslModeComboBox.SelectedItem = postgreSql.SslMode;
        PasswordInput.Password = credentialStore.ReadPostgreSqlPassword() ?? string.Empty;

        if (settings.DatabaseProvider == DatabaseProvider.PostgreSql || recoveryMode)
            PostgreSqlRadio.IsChecked = true;

        if (recoveryMode)
        {
            HeadingText.Text = "Database startup needs attention";
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
            StatusText.Text = failureMessage;
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
            if (_settingsService.GetSettings().PendingDatabaseProvider == DatabaseProvider.Sqlite)
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

            if (
                _recoveryMode
                && settings.PendingDatabaseProvider == DatabaseProvider.Sqlite
                && await _switchService!.TryCompletePendingSwitchAsync()
            )
            {
                DialogResult = true;
                return;
            }

            if (SqliteRadio.IsChecked == true)
            {
                if (_recoveryMode && settings.DatabaseProvider == DatabaseProvider.PostgreSql)
                {
                    var answer = MessageBox.Show(
                        "Copy PostgreSQL history to SQLite now? The current local database will be backed up and replaced with the copied history.",
                        Title,
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information,
                        MessageBoxResult.No
                    );
                    if (answer != MessageBoxResult.Yes)
                        return;
                    settings.PendingDatabaseProvider = DatabaseProvider.Sqlite;
                    settings.PendingDatabaseImport = true;
                    settings.PendingDatabaseReplace = false;
                    settings.PendingDatabaseSwitchId ??= Guid.NewGuid().ToString("N");
                }
                else if (_recoveryMode)
                {
                    await _switchService!.CancelPendingSwitchAsync();
                    DialogResult = true;
                    return;
                }
                else
                {
                    settings.DatabaseProvider = DatabaseProvider.Sqlite;
                    settings.ClearPendingDatabaseSwitch();
                }
            }
            else
            {
                var postgreSql = ReadPostgreSqlSettings();
                var password = ReadPassword();
                StatusText.Text = "Testing connection...";
                if (_recoveryMode && settings.PendingDatabaseProvider.HasValue)
                {
                    if (!DatabaseConfigurationService.IsSamePostgreSqlDatabase(settings.PostgreSql, postgreSql))
                        throw new InvalidOperationException(
                            "Cancel the pending change before selecting a different database"
                        );
                    await DatabaseConfigurationService.TestPostgreSqlReadAsync(postgreSql, password);
                }
                else
                    await DatabaseConfigurationService.TestPostgreSqlAsync(postgreSql, password);
                _credentialStore.WritePostgreSqlPassword(password);
                settings.PostgreSql = postgreSql;

                if (
                    !_recoveryMode
                    || (
                        !settings.PendingDatabaseProvider.HasValue
                        && settings.DatabaseProvider != DatabaseProvider.PostgreSql
                    )
                )
                {
                    settings.PendingDatabaseProvider = DatabaseProvider.PostgreSql;
                    settings.PendingDatabaseImport = DatabaseConfigurationService.HasSqliteHistory();
                    settings.PendingDatabaseReplace = false;
                    settings.PendingDatabaseSwitchId = Guid.NewGuid().ToString("N");
                }
            }

            settings.IsFirstLaunch = false;
            _settingsService.SaveSettings(settings);
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
