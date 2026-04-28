using System.Reflection;
using Microsoft.Win32;
using Serilog;

namespace KeyPulse.Services;

public class StartupRegistrationService
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private readonly string _appName;

    public StartupRegistrationService()
    {
        _appName = Assembly.GetExecutingAssembly().GetName().Name ?? "KeyPulse";
    }

    public bool IsEnabled()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = runKey?.GetValue(_appName) as string;
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
            using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            var command = BuildCommand();
            runKey.SetValue(_appName, command, RegistryValueKind.String);
            Log.Information("Enabled startup registration for {AppName}; Command={Command}", _appName, command);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to enable startup registration for {AppName}", _appName);
            throw;
        }
    }

    public void Disable()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (runKey?.GetValue(_appName) == null)
                return;

            runKey.DeleteValue(_appName, throwOnMissingValue: false);
            Log.Information("Disabled startup registration for {AppName}", _appName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to disable startup registration for {AppName}", _appName);
            throw;
        }
    }

    private static string BuildCommand()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new InvalidOperationException("Unable to determine current executable path for startup registration");

        var quotedPath = $"\"{executablePath}\"";
        return $"{quotedPath} --startup";
    }
}
