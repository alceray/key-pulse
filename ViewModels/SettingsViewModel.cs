using System.Windows;
using System.Windows.Input;
using KeyPulse.Configuration;
using KeyPulse.Helpers;
using KeyPulse.Models;
using KeyPulse.Services;
using KeyPulse.ViewModels.Settings;
using Serilog;

namespace KeyPulse.ViewModels;

public class SettingsViewModel : ToastMessageViewModelBase
{
    private readonly AppSettingsService _appSettingsService;
    private readonly StartupRegistrationService _startupRegistrationService;
    private readonly UpdateService _updateService;
    private readonly IDatabaseCredentialStore _databaseCredentialStore;
    private bool _launchOnLogin;
    private bool _autoInstallUpdates;
    private bool _closeToTray;
    private bool _darkMode;
    private RetentionOption _selectedRetentionOption = RetentionOptions.All[0];
    private bool _isCheckingUpdates;
    private bool _isUpdateAvailable;
    private string? _latestUpdateVersion;
    private bool _suppressAutoSave;
    private DatabaseProvider _activeDatabaseProvider;
    private DatabaseProvider _selectedDatabaseProvider;
    private string _postgreSqlHost = "localhost";
    private int _postgreSqlPort = 5432;
    private string _postgreSqlDatabase = "";
    private string _postgreSqlUsername = "";
    private string _postgreSqlPassword = "";
    private PostgreSqlSslMode _postgreSqlSslMode = PostgreSqlSslMode.Prefer;
    private PostgreSqlConnectionSettings _loadedPostgreSql = new();
    private bool _isEditingConnection;
    private bool _hasPendingDatabaseChange;
    private string _pendingDatabaseSummary = "";
    private DatabaseConnectionSettingsService ConnectionSettings => new(_appSettingsService, _databaseCredentialStore);

    public SettingsViewModel(
        AppSettingsService appSettingsService,
        StartupRegistrationService startupRegistrationService,
        UpdateService updateService,
        IDatabaseCredentialStore databaseCredentialStore
    )
    {
        _appSettingsService = appSettingsService;
        _startupRegistrationService = startupRegistrationService;
        _updateService = updateService;
        _databaseCredentialStore = databaseCredentialStore;

        UpdateActionCommand = new AsyncRelayCommand(_ => RunUpdateActionAsync(), _ => !_isCheckingUpdates);
        TestDatabaseConnectionCommand = new AsyncRelayCommand(
            _ => TestDatabaseConnectionAsync(),
            _ => !_hasPendingDatabaseChange
        );
        SaveAndRestartDatabaseCommand = new AsyncRelayCommand(_ => SaveAndRestartDatabaseAsync());

        _appSettingsService.SettingsChanged += OnSettingsChanged;
        _updateService.UpdateStatusChanged += OnUpdateStatusChanged;

        _isUpdateAvailable = _updateService.UpdateAvailable;
        _latestUpdateVersion = _updateService.LatestVersion;

        LoadSettings();
    }

    public bool LaunchOnLogin
    {
        get => _launchOnLogin;
        set
        {
            if (_launchOnLogin == value)
                return;

            _launchOnLogin = value;
            OnPropertyChanged();

            if (!_suppressAutoSave)
                SaveSettings(nameof(AppUserSettings.LaunchOnLogin), value);
        }
    }

    public bool AutoInstallUpdates
    {
        get => _autoInstallUpdates;
        set
        {
            if (_autoInstallUpdates == value)
                return;

            _autoInstallUpdates = value;
            OnPropertyChanged();

            if (!_suppressAutoSave)
                SaveSettings(nameof(AppUserSettings.AutoInstallUpdates), value);
        }
    }

    public bool DarkMode
    {
        get => _darkMode;
        set
        {
            if (_darkMode == value)
                return;
            _darkMode = value;
            OnPropertyChanged();
            if (!_suppressAutoSave)
                SaveSettings(nameof(AppUserSettings.DarkMode), value);
        }
    }

    public bool CloseToTray
    {
        get => _closeToTray;
        set
        {
            if (_closeToTray == value)
                return;

            _closeToTray = value;
            OnPropertyChanged();

            if (!_suppressAutoSave)
                SaveSettings(nameof(AppUserSettings.CloseToTray), value);
        }
    }

    // Close-to-tray only has meaning when a tray exists. Windowed sessions have no tray, so closing
    // always exits there; hide the option rather than show a control that does nothing.
    public bool ShowCloseToTrayOption => App.RunInBackground;

    public IReadOnlyList<PostgreSqlSslMode> PostgreSqlSslModeChoices { get; } = Enum.GetValues<PostgreSqlSslMode>();
    public ICommand TestDatabaseConnectionCommand { get; }
    public ICommand SaveAndRestartDatabaseCommand { get; }

    public DatabaseProvider SelectedDatabaseProvider
    {
        get => _selectedDatabaseProvider;
        set
        {
            if (_selectedDatabaseProvider == value)
                return;
            _selectedDatabaseProvider = value;
            _isEditingConnection = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UseSqliteStorage));
            OnPropertyChanged(nameof(UsePostgreSqlStorage));
            OnPropertyChanged(nameof(IsStorageProviderChanged));
            RaiseConnectionStateChanged();
        }
    }

    public bool UseSqliteStorage
    {
        get => SelectedDatabaseProvider == DatabaseProvider.Sqlite;
        set
        {
            if (value)
                SelectedDatabaseProvider = DatabaseProvider.Sqlite;
        }
    }

    public bool UsePostgreSqlStorage
    {
        get => SelectedDatabaseProvider == DatabaseProvider.PostgreSql;
        set
        {
            if (value)
                SelectedDatabaseProvider = DatabaseProvider.PostgreSql;
        }
    }

    public bool IsStorageProviderChanged => SelectedDatabaseProvider != _activeDatabaseProvider;

    private bool IsPostgreSqlSelected => SelectedDatabaseProvider == DatabaseProvider.PostgreSql;

    public bool IsPostgreSqlActive => _activeDatabaseProvider == DatabaseProvider.PostgreSql;

    public bool IsSqliteActive => _activeDatabaseProvider == DatabaseProvider.Sqlite;

    public bool ShowPostgreSqlSummary => IsPostgreSqlSelected && IsPostgreSqlActive && !_isEditingConnection;

    public bool ShowPostgreSqlForm => IsPostgreSqlSelected && (!IsPostgreSqlActive || _isEditingConnection);

    public bool ShowDatabaseActions => _hasPendingDatabaseChange || IsStorageProviderChanged || _isEditingConnection;

    public bool CanEditConnectionTarget => !_hasPendingDatabaseChange;
    public bool HasPendingDatabaseChange => _hasPendingDatabaseChange;
    public string PendingDatabaseSummary => _pendingDatabaseSummary;

    public string PostgreSqlSummary => _loadedPostgreSql.Describe();

    public string PostgreSqlSslSummary => $"SSL mode: {_loadedPostgreSql.SslMode}";

    public void BeginEditConnection()
    {
        if (_isEditingConnection || _hasPendingDatabaseChange)
            return;

        _isEditingConnection = true;
        RaiseConnectionStateChanged();
    }

    private void RaiseConnectionStateChanged()
    {
        OnPropertyChanged(nameof(HasPendingDatabaseChange));
        OnPropertyChanged(nameof(PendingDatabaseSummary));
        AsyncRelayCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(ShowPostgreSqlSummary));
        OnPropertyChanged(nameof(ShowPostgreSqlForm));
        OnPropertyChanged(nameof(ShowDatabaseActions));
        OnPropertyChanged(nameof(IsPostgreSqlActive));
        OnPropertyChanged(nameof(IsSqliteActive));
        OnPropertyChanged(nameof(CanEditConnectionTarget));
        OnPropertyChanged(nameof(PostgreSqlSummary));
        OnPropertyChanged(nameof(PostgreSqlSslSummary));
    }

    public string PostgreSqlHost
    {
        get => _postgreSqlHost;
        set => SetDatabaseField(ref _postgreSqlHost, value);
    }

    public int PostgreSqlPort
    {
        get => _postgreSqlPort;
        set => SetDatabaseField(ref _postgreSqlPort, value);
    }

    public string PostgreSqlDatabase
    {
        get => _postgreSqlDatabase;
        set => SetDatabaseField(ref _postgreSqlDatabase, value);
    }

    public string PostgreSqlUsername
    {
        get => _postgreSqlUsername;
        set => SetDatabaseField(ref _postgreSqlUsername, value);
    }

    public string PostgreSqlPassword
    {
        get => _postgreSqlPassword;
        set => SetDatabaseField(ref _postgreSqlPassword, value);
    }

    public PostgreSqlSslMode PostgreSqlSslMode
    {
        get => _postgreSqlSslMode;
        set => SetDatabaseField(ref _postgreSqlSslMode, value);
    }

    private void SetDatabaseField<T>(
        ref T field,
        T value,
        [System.Runtime.CompilerServices.CallerMemberName] string? name = null
    )
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        OnPropertyChanged(name);
    }

    public IReadOnlyList<RetentionOption> RetentionChoices => RetentionOptions.All;

    public RetentionOption SelectedRetentionOption
    {
        get => _selectedRetentionOption;
        set
        {
            if (_selectedRetentionOption == value || value is null)
                return;

            _selectedRetentionOption = value;
            OnPropertyChanged();

            if (!_suppressAutoSave)
                SaveSettings(nameof(AppUserSettings.ActivityRetentionMonths), value.Months);
        }
    }

    public string CurrentVersionDisplay => $"Version: v{_updateService.CurrentVersion}";

    public string UpdateActionButtonText =>
        _isUpdateAvailable && !string.IsNullOrWhiteSpace(_latestUpdateVersion)
            ? $"Update to v{_latestUpdateVersion}"
            : "Check for Updates";

    public ICommand UpdateActionCommand { get; }

    private async Task RunUpdateActionAsync()
    {
        if (_isCheckingUpdates)
            return;

        if (_isUpdateAvailable && !string.IsNullOrWhiteSpace(_latestUpdateVersion))
        {
            _updateService.InstallUpdate();
            return;
        }

        try
        {
            _isCheckingUpdates = true;
            AsyncRelayCommand.RaiseCanExecuteChanged();
            await _updateService.CheckForUpdatesAsync();
            SyncUpdateStateFromService();

            if (!_isUpdateAvailable)
                ToastMessage = "No new updates available.";
        }
        catch (Exception ex)
        {
            ToastMessage = "Update check failed. Check logs for details.";
            Log.Error(ex, "Manual update check failed");
        }
        finally
        {
            _isCheckingUpdates = false;
            AsyncRelayCommand.RaiseCanExecuteChanged();
        }
    }

    private void OnUpdateStatusChanged(UpdateService.UpdateAvailableEventArgs args)
    {
        void Apply()
        {
            _isUpdateAvailable = args.Available;
            _latestUpdateVersion = args.LatestVersion;
            OnPropertyChanged(nameof(UpdateActionButtonText));
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            Apply();
            return;
        }

        dispatcher.BeginInvoke(new Action(Apply));
    }

    private void SyncUpdateStateFromService()
    {
        _isUpdateAvailable = _updateService.UpdateAvailable;
        _latestUpdateVersion = _updateService.LatestVersion;
        OnPropertyChanged(nameof(UpdateActionButtonText));
    }

    private void LoadSettings()
    {
        _suppressAutoSave = true;
        try
        {
            var settings = _appSettingsService.GetSettings();
            LaunchOnLogin = settings.LaunchOnLogin;
            AutoInstallUpdates = settings.AutoInstallUpdates;
            CloseToTray = settings.CloseToTray;
            DarkMode = settings.DarkMode;
            SelectedRetentionOption = RetentionOptions.FromMonths(settings.ActivityRetentionMonths);
            ToastMessage = string.Empty;
            LoadDatabaseSettings(settings);

            // Reflect the actual registration state so the UI matches the machine state.
            if (!_startupRegistrationService.IsEnabled() && LaunchOnLogin)
                LaunchOnLogin = false;
        }
        finally
        {
            _suppressAutoSave = false;
        }
    }

    private void LoadDatabaseSettings(AppUserSettings settings)
    {
        _activeDatabaseProvider = settings.DatabaseProvider;
        _hasPendingDatabaseChange = settings.PendingDatabaseProvider.HasValue;
        _pendingDatabaseSummary = _hasPendingDatabaseChange
            ? $"Pending: {(settings.PendingDatabaseProvider == DatabaseProvider.PostgreSql ? (settings.PendingPostgreSql ?? settings.PostgreSql).Describe() : "local SQLite")}. Restart to finish, or cancel before making another change."
            : "";
        SelectedDatabaseProvider = settings.DatabaseProvider;
        _loadedPostgreSql = settings.PostgreSql.Copy();
        PostgreSqlHost = settings.PostgreSql.Host;
        PostgreSqlPort = settings.PostgreSql.Port;
        PostgreSqlDatabase = settings.PostgreSql.Database;
        PostgreSqlUsername = settings.PostgreSql.Username;
        PostgreSqlSslMode = settings.PostgreSql.SslMode;
        try
        {
            PostgreSqlPassword =
                _databaseCredentialStore.ReadPostgreSqlPassword(settings.PostgreSqlCredentialReference) ?? "";
        }
        catch (Exception ex)
        {
            PostgreSqlPassword = "";
            Log.Warning(ex, "The saved database password could not be loaded");
            ToastMessage =
                "The saved database password could not be loaded. Enter it again to test or save the connection.";
        }
        OnPropertyChanged(nameof(IsStorageProviderChanged));
        RaiseConnectionStateChanged();
    }

    public async Task CancelDatabaseChangesAsync()
    {
        try
        {
            _isEditingConnection = false;
            if (_appSettingsService.GetSettings().PendingDatabaseProvider.HasValue)
            {
                using var destinationLock = new DatabaseInstanceLock();
                var switches = new DatabaseSwitchService(
                    _appSettingsService,
                    _databaseCredentialStore,
                    destinationLock
                );
                if (await switches.CancelPendingSwitchAsync())
                    ((App)Application.Current).Restart();
            }
            LoadDatabaseSettings(_appSettingsService.GetSettings());
        }
        catch (Exception ex)
        {
            ToastMessage = $"The pending change could not be canceled: {ex.Message}";
        }
    }

    private PostgreSqlConnectionSettings ReadPostgreSqlSettings() =>
        new()
        {
            Host = PostgreSqlHost.Trim(),
            Port = PostgreSqlPort,
            Database = PostgreSqlDatabase.Trim(),
            Username = PostgreSqlUsername.Trim(),
            SslMode = PostgreSqlSslMode,
        };

    private string ReadPostgreSqlPassword() =>
        string.IsNullOrEmpty(PostgreSqlPassword)
            ? throw new InvalidOperationException("Enter the PostgreSQL password")
            : PostgreSqlPassword;

    private async Task TestDatabaseConnectionAsync()
    {
        try
        {
            await DatabaseConfigurationService.TestPostgreSqlAsync(ReadPostgreSqlSettings(), ReadPostgreSqlPassword());
            ToastMessage = "Database connection successful.";
        }
        catch (Exception ex)
        {
            ToastMessage = $"Connection failed: {ex.Message}";
            Log.Debug(ex, "PostgreSQL connection test failed");
        }
    }

    private async Task SaveAndRestartDatabaseAsync()
    {
        var saved = false;
        try
        {
            var settings = _appSettingsService.GetSettings();
            if (!settings.PendingDatabaseProvider.HasValue)
            {
                if (SelectedDatabaseProvider == DatabaseProvider.PostgreSql)
                {
                    var postgreSql = ReadPostgreSqlSettings();
                    var password = ReadPostgreSqlPassword();
                    await DatabaseConfigurationService.TestPostgreSqlAsync(postgreSql, password);
                    var copyHistory =
                        settings.DatabaseProvider == DatabaseProvider.Sqlite
                        || !DatabaseConfigurationService.IsSamePostgreSqlDatabase(settings.PostgreSql, postgreSql);
                    if (copyHistory)
                    {
                        var source =
                            settings.DatabaseProvider == DatabaseProvider.PostgreSql
                                ? settings.PostgreSql.Describe()
                                : "local SQLite";
                        if (
                            MessageBox.Show(
                                $"Restart KeyPulse now and copy history from {source} to {postgreSql.Describe()}? "
                                    + "Source history will be retained. Any existing KeyPulse history in the destination will be replaced.",
                                AppConstants.App.DefaultName,
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Warning,
                                MessageBoxResult.No
                            ) != MessageBoxResult.Yes
                        )
                            return;
                        ConnectionSettings.SchedulePostgreSql(
                            settings,
                            postgreSql,
                            password,
                            copyHistory: true,
                            replaceExisting: true
                        );
                    }
                    else
                        ConnectionSettings.UpdateAuthentication(settings, postgreSql, password);
                }
                else if (settings.DatabaseProvider == DatabaseProvider.PostgreSql)
                {
                    if (
                        MessageBox.Show(
                            "Restart KeyPulse now and copy PostgreSQL history to SQLite? The current local database will be backed up and replaced with the copied history.",
                            AppConstants.App.DefaultName,
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Information,
                            MessageBoxResult.No
                        ) != MessageBoxResult.Yes
                    )
                        return;
                    ConnectionSettings.ScheduleSqlite(settings);
                }
            }
            saved = true;
            _isEditingConnection = false;
            RaiseConnectionStateChanged();
            ToastMessage = "Restarting KeyPulse...";
            var app =
                Application.Current as App
                ?? throw new InvalidOperationException("The running KeyPulse application is unavailable");
            app.Restart();
        }
        catch (Exception ex)
        {
            saved |= _appSettingsService.GetSettings().PendingDatabaseProvider.HasValue;
            ToastMessage = saved
                ? $"Database change saved, but restart failed: {ex.Message}. Close and reopen KeyPulse to finish."
                : $"Database change failed: {ex.Message}";
            Log.Warning(
                ex,
                saved
                    ? "Application restart failed after saving database settings"
                    : "Database setting could not be saved"
            );
        }
    }

    private void SaveSettings(string changedSetting, object changedValue)
    {
        try
        {
            // Read-modify-write so fields not edited on this page are preserved.
            var settings = _appSettingsService.GetSettings();
            settings.LaunchOnLogin = LaunchOnLogin;
            settings.AutoInstallUpdates = AutoInstallUpdates;
            settings.CloseToTray = CloseToTray;
            settings.DarkMode = DarkMode;
            settings.ActivityRetentionMonths = SelectedRetentionOption.Months;

            _appSettingsService.SaveSettings(settings);

            if (changedSetting == nameof(AppUserSettings.LaunchOnLogin))
            {
                if (settings.LaunchOnLogin)
                    _startupRegistrationService.Enable();
                else
                    _startupRegistrationService.Disable();
            }

            ToastMessage = "Settings saved.";
            Log.Debug("Setting updated: {Setting}={Value}", changedSetting, changedValue);
        }
        catch (Exception ex)
        {
            ToastMessage = "Failed to save settings. Check logs for details.";
            Log.Error(ex, "Failed to save settings");
            if (changedSetting == nameof(AppUserSettings.DarkMode))
            {
                _darkMode = AppColorPalette.IsDark;
                OnPropertyChanged(nameof(DarkMode));
            }
        }
    }

    private void OnSettingsChanged(AppUserSettings settings)
    {
        _suppressAutoSave = true;
        try
        {
            LaunchOnLogin = settings.LaunchOnLogin;
            AutoInstallUpdates = settings.AutoInstallUpdates;
            CloseToTray = settings.CloseToTray;
            DarkMode = settings.DarkMode;
            SelectedRetentionOption = RetentionOptions.FromMonths(settings.ActivityRetentionMonths);
            LoadDatabaseSettings(settings);
        }
        finally
        {
            _suppressAutoSave = false;
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _appSettingsService.SettingsChanged -= OnSettingsChanged;
        _updateService.UpdateStatusChanged -= OnUpdateStatusChanged;
    }
}
