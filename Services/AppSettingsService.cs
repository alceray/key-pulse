using System.IO;
using System.Text.Json;
using KeyPulse.Models;
using Serilog;

namespace KeyPulse.Services;

public class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _syncRoot = new();
    private readonly string _settingsFilePath;
    private readonly string _appName;
    public event Action<AppUserSettings>? SettingsChanged;

    public AppSettingsService()
    {
        _appName = AppDomain.CurrentDomain.FriendlyName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var settingsDirectory = Path.Combine(appData, _appName);
        Directory.CreateDirectory(settingsDirectory);
        _settingsFilePath = Path.Combine(settingsDirectory, "settings.json");
    }

    public AppUserSettings GetSettings()
    {
        lock (_syncRoot)
        {
            try
            {
                if (!File.Exists(_settingsFilePath))
                    return new AppUserSettings();

                var json = File.ReadAllText(_settingsFilePath);
                return JsonSerializer.Deserialize<AppUserSettings>(json) ?? new AppUserSettings();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to read app settings; using defaults");
                return new AppUserSettings();
            }
        }
    }

    public void SaveSettings(AppUserSettings settings)
    {
        Action<AppUserSettings>? handlers;

        lock (_syncRoot)
        {
            try
            {
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(_settingsFilePath, json);
                handlers = SettingsChanged;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save app settings");
                throw;
            }
        }

        handlers?.Invoke(settings);
    }
}
