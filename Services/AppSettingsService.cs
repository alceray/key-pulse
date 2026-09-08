using System.IO;
using System.Text.Json;
using KeyPulse.Configuration;
using KeyPulse.Models;
using Serilog;

namespace KeyPulse.Services;

public class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _syncRoot = new();
    private readonly string _settingsFilePath;
    public event Action<AppUserSettings>? SettingsChanged;

    public AppSettingsService()
        : this(AppDataPaths.GetPath(AppConstants.Paths.SettingsFileName)) { }

    internal AppSettingsService(string settingsFilePath)
    {
        _settingsFilePath = Path.GetFullPath(settingsFilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsFilePath)!);
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
                Log.Warning(ex, "Failed to read settings; using defaults");
                return new AppUserSettings();
            }
        }
    }

    public void SaveSettings(AppUserSettings settings)
    {
        Action<AppUserSettings>? handlers;

        lock (_syncRoot)
        {
            var temporaryPath = _settingsFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, settings, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(_settingsFilePath))
                    File.Replace(temporaryPath, _settingsFilePath, null);
                else
                    File.Move(temporaryPath, _settingsFilePath);
                handlers = SettingsChanged;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save settings");
                throw;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        handlers?.Invoke(settings);
    }

    // Credential cleanup must fail closed when durable settings cannot be read.
    internal AppUserSettings ReadPersistedSettings()
    {
        lock (_syncRoot)
            return JsonSerializer.Deserialize<AppUserSettings>(File.ReadAllText(_settingsFilePath))
                ?? throw new InvalidDataException("Saved settings are empty");
    }
}
