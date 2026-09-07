using System.Windows;
using KeyPulse.Configuration;
using KeyPulse.Helpers;
using KeyPulse.Models;
using Serilog;

namespace KeyPulse.Services;

/// <summary>Applies the saved theme on the UI thread and notifies non-XAML presentation code.</summary>
public sealed class ThemeService : IDisposable
{
    private readonly AppSettingsService _settings;
    private bool _disposed;
    public event Action? ThemeChanged;

    public ThemeService(AppSettingsService settings)
    {
        _settings = settings;
        _settings.SettingsChanged += OnSettingsChanged;
        ApplyTheme(settings.GetSettings().DarkMode);
    }

    // Also used before DI exists, so database setup/recovery windows start in the saved theme.
    internal static void ApplyTheme(bool darkMode)
    {
        var application = Application.Current;
        if (application == null)
            return;
        application.Dispatcher.VerifyAccess();
        AppColorPalette.Apply(application.Resources, darkMode);
    }

    private void OnSettingsChanged(AppUserSettings settings)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (_disposed || !ShutdownDispose.IsDispatcherUsable(dispatcher))
            return;

        void Apply()
        {
            if (_disposed || AppColorPalette.IsDark == settings.DarkMode)
                return;
            ApplyTheme(settings.DarkMode);
            ThemeChanged?.Invoke();
        }

        if (dispatcher!.CheckAccess())
            Apply();
        else
            dispatcher.BeginInvoke(Apply);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            Log.Debug("Theme updates are already stopped");
            return;
        }
        _disposed = true;
        _settings.SettingsChanged -= OnSettingsChanged;
    }
}
