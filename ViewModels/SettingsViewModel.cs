using System.Windows.Input;
using KeyPulse.Helpers;
using KeyPulse.Models;
using KeyPulse.Services;
using Serilog;

namespace KeyPulse.ViewModels;

public class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly AppSettingsService _appSettingsService;
    private readonly StartupRegistrationService _startupRegistrationService;
    private bool _launchOnLogin;
    private string _statusMessage = "";

    public SettingsViewModel(
        AppSettingsService appSettingsService,
        StartupRegistrationService startupRegistrationService
    )
    {
        _appSettingsService = appSettingsService;
        _startupRegistrationService = startupRegistrationService;

        SaveSettingsCommand = new RelayCommand(_ => SaveSettings());
        ReloadSettingsCommand = new RelayCommand(_ => LoadSettings());
        _appSettingsService.SettingsChanged += OnSettingsChanged;

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
        }
    }

    public ICommand SaveSettingsCommand { get; }

    public ICommand ReloadSettingsCommand { get; }

    private void LoadSettings()
    {
        var settings = _appSettingsService.GetSettings();
        LaunchOnLogin = settings.LaunchOnLogin;

        // Reflect the actual registration state so the UI matches the machine state.
        if (!_startupRegistrationService.IsEnabled() && LaunchOnLogin)
            LaunchOnLogin = false;

        StatusMessage = "";
    }

    private void SaveSettings()
    {
        try
        {
            var settings = new AppUserSettings { LaunchOnLogin = LaunchOnLogin };

            _appSettingsService.SaveSettings(settings);

            if (settings.LaunchOnLogin)
                _startupRegistrationService.Enable();
            else
                _startupRegistrationService.Disable();

            StatusMessage = "Settings saved.";
            Log.Information(
                "Settings updated from SettingsView: LaunchOnLogin={LaunchOnLogin}",
                settings.LaunchOnLogin
            );
        }
        catch (Exception ex)
        {
            StatusMessage = "Failed to save settings. Check logs for details.";
            Log.Error(ex, "Failed to save settings from SettingsView");
        }
    }

    private void OnSettingsChanged(AppUserSettings settings)
    {
        LaunchOnLogin = settings.LaunchOnLogin;
    }

    public void Dispose()
    {
        _appSettingsService.SettingsChanged -= OnSettingsChanged;
        GC.SuppressFinalize(this);
    }
}
