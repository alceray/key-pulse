using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using KeyPulse.Helpers;
using KeyPulse.Models;
using KeyPulse.Services;
using Serilog;

namespace KeyPulse.ViewModels;

public class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly AppSettingsService _appSettingsService;
    private readonly StartupRegistrationService _startupRegistrationService;
    private readonly UpdateService _updateService;
    private bool _launchOnLogin;
    private bool _autoInstallUpdates;
    private bool _isCheckingUpdates;
    private bool _isUpdateAvailable;
    private string? _latestUpdateVersion;
    private bool _suppressAutoSave;
    private string _statusMessage = string.Empty;
    private readonly DispatcherTimer _statusTimer;

    public SettingsViewModel(
        AppSettingsService appSettingsService,
        StartupRegistrationService startupRegistrationService,
        UpdateService updateService
    )
    {
        _appSettingsService = appSettingsService;
        _startupRegistrationService = startupRegistrationService;
        _updateService = updateService;

        UpdateActionCommand = new AsyncRelayCommand(_ => RunUpdateActionAsync(), _ => !_isCheckingUpdates);
        _appSettingsService.SettingsChanged += OnSettingsChanged;
        _updateService.UpdateStatusChanged += OnUpdateStatusChanged;

        _isUpdateAvailable = _updateService.UpdateAvailable;
        _latestUpdateVersion = _updateService.LatestVersion;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            StatusMessage = string.Empty;
        };

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
                SaveSettings();
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
                SaveSettings();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value)
                return;

            _statusMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusVisibility));

            if (!string.IsNullOrEmpty(value))
            {
                _statusTimer.Stop();
                _statusTimer.Start();
            }
        }
    }

    public Visibility StatusVisibility =>
        string.IsNullOrEmpty(_statusMessage) ? Visibility.Collapsed : Visibility.Visible;

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
                StatusMessage = "No new updates available.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Update check failed. Check logs for details.";
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

            // Reflect the actual registration state so the UI matches the machine state.
            if (!_startupRegistrationService.IsEnabled() && LaunchOnLogin)
                LaunchOnLogin = false;

            StatusMessage = string.Empty;
        }
        finally
        {
            _suppressAutoSave = false;
        }
    }

    private void SaveSettings()
    {
        try
        {
            var settings = new AppUserSettings
            {
                LaunchOnLogin = LaunchOnLogin,
                AutoInstallUpdates = AutoInstallUpdates,
            };

            _appSettingsService.SaveSettings(settings);

            if (settings.LaunchOnLogin)
                _startupRegistrationService.Enable();
            else
                _startupRegistrationService.Disable();

            StatusMessage = "Settings saved.";
            Log.Debug(
                "Settings updated: LaunchOnLogin={LaunchOnLogin}, AutoInstallUpdates={AutoInstallUpdates}",
                settings.LaunchOnLogin,
                settings.AutoInstallUpdates
            );
        }
        catch (Exception ex)
        {
            StatusMessage = "Failed to save settings. Check logs for details.";
            Log.Error(ex, "Failed to save settings");
        }
    }

    private void OnSettingsChanged(AppUserSettings settings)
    {
        _suppressAutoSave = true;
        try
        {
            LaunchOnLogin = settings.LaunchOnLogin;
            AutoInstallUpdates = settings.AutoInstallUpdates;
        }
        finally
        {
            _suppressAutoSave = false;
        }
    }

    public void Dispose()
    {
        _statusTimer.Stop();
        _appSettingsService.SettingsChanged -= OnSettingsChanged;
        _updateService.UpdateStatusChanged -= OnUpdateStatusChanged;
    }
}
