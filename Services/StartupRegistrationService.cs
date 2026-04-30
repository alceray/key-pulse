using KeyPulse.Configuration;
using Microsoft.Win32;
using Serilog;

namespace KeyPulse.Services;

public class StartupRegistrationService
{
    private static readonly string AppName = AppConstants.App.DefaultName;

    public bool IsEnabled()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(AppConstants.Registry.RunKeyPath, false);
            var value = runKey?.GetValue(AppName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read startup registration state");
            return false;
        }
    }

    public void Enable()
    {
        try
        {
            using var runKey = Registry.CurrentUser.CreateSubKey(AppConstants.Registry.RunKeyPath, true);
            var command = BuildCommand();
            runKey.SetValue(AppName, command, RegistryValueKind.String);
            Log.Information("Enabled startup registration for {AppName}; Command={Command}", AppName, command);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to enable startup registration for {AppName}", AppName);
            throw;
        }
    }

    public void Disable()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(AppConstants.Registry.RunKeyPath, true);
            if (runKey?.GetValue(AppName) == null)
                return;

            runKey.DeleteValue(AppName, false);
            Log.Information("Disabled startup registration for {AppName}", AppName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to disable startup registration for {AppName}", AppName);
            throw;
        }
    }

    private static string BuildCommand()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new InvalidOperationException("Unable to determine current executable path for startup registration");

        var quotedPath = $"\"{executablePath}\"";
        return $"{quotedPath} {AppConstants.App.StartupArgument}";
    }
}
