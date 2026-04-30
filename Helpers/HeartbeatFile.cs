using System.Globalization;
using System.IO;
using KeyPulse.Configuration;
using Serilog;

namespace KeyPulse.Helpers;

/// <summary>
/// Manages a heartbeat file written periodically while the app is running.
/// Used by DataService.RecoverFromCrash to determine approximately when a crash occurred.
/// </summary>
public static class HeartbeatFile
{
    private static string FilePath
    {
        get => AppDataPaths.GetPath(AppConstants.Paths.HeartbeatFileName);
    }

    /// <summary>
    /// Writes the current timestamp to the heartbeat file.
    /// </summary>
    public static void Write()
    {
        try
        {
            File.WriteAllText(FilePath, DateTime.Now.ToString("O"));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to write heartbeat file");
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
            {
                Log.Debug("Heartbeat read skipped; file does not exist at {HeartbeatPath}", FilePath);
                return null;
            }

            var text = File.ReadAllText(FilePath).Trim();
            if (DateTime.TryParse(text, null, DateTimeStyles.RoundtripKind, out var dt))
                return dt;

            Log.Warning(
                "Heartbeat read failed because file content was not a valid timestamp at {HeartbeatPath}",
                FilePath
            );
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read heartbeat file");
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
            Log.Error(ex, "Failed to clear heartbeat file");
        }
    }
}
