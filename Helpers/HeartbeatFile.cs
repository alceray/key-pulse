using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace KeyPulse.Helpers;

/// <summary>
/// Manages a heartbeat file written periodically while the app is running.
/// Used by DataService.RecoverFromCrash to determine approximately when a crash occurred.
/// </summary>
public static class HeartbeatFile
{
    private static string FilePath
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var appName = Assembly.GetExecutingAssembly().GetName().Name ?? "KeyPulse";
            return Path.Combine(appData, appName, "heartbeat.txt");
        }
    }

    /// <summary>
    /// Writes the current UTC timestamp to the heartbeat file.
    /// </summary>
    public static void Write()
    {
        try
        {
            File.WriteAllText(FilePath, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"HeartbeatFile.Write failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads the last heartbeat timestamp. Returns null if the file doesn't exist or is unreadable.
    /// </summary>
    public static DateTime? Read()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;
            var text = File.ReadAllText(FilePath).Trim();
            return DateTime.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                ? dt.ToLocalTime()
                : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"HeartbeatFile.Read failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Deletes the heartbeat file on clean shutdown so stale values aren't read next launch.
    /// </summary>
    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"HeartbeatFile.Clear failed: {ex.Message}");
        }
    }
}
