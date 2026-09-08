using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace KeyPulse.Services;

internal static class AppRestartService
{
    internal const string ParentArgument = "--restart-parent";

    internal static ProcessStartInfo CreateStartInfo(string executablePath, string[] commandLine, int parentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (parentId <= 0)
            throw new ArgumentOutOfRangeException(nameof(parentId));

        var start = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        // A framework-dependent app can be launched as either its apphost or "dotnet app.dll".
        if (
            string.Equals(
                Path.GetFileNameWithoutExtension(executablePath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            if (commandLine.Length == 0)
                throw new InvalidOperationException("The application path is unavailable for restart");
            start.ArgumentList.Add(commandLine[0]);
        }
        for (var index = 1; index < commandLine.Length; index++)
        {
            // A second restart must wait for this process, not the already-exited grandparent.
            if (commandLine[index] == ParentArgument)
            {
                index++;
                continue;
            }
            start.ArgumentList.Add(commandLine[index]);
        }
        start.ArgumentList.Add(ParentArgument);
        start.ArgumentList.Add(parentId.ToString(CultureInfo.InvariantCulture));
        return start;
    }

    internal static async Task<bool> WaitForPreviousInstanceAsync(
        string[] arguments,
        CancellationToken cancellationToken = default
    )
    {
        var index = Array.IndexOf(arguments, ParentArgument);
        if (index < 0)
            return false;
        if (
            index + 1 >= arguments.Length
            || !int.TryParse(arguments[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var parentId)
            || parentId <= 0
            || parentId == Environment.ProcessId
        )
            throw new InvalidOperationException("The application restart request is invalid");

        Process previous;
        try
        {
            previous = Process.GetProcessById(parentId);
        }
        catch (ArgumentException)
        {
            // The previous instance can finish shutting down before this process starts.
            return true;
        }
        using (previous)
        {
            // Wait for full process exit, not merely release of the single-instance mutex:
            // shutdown still needs to flush input, close sessions, and dispose database locks.
            await previous.WaitForExitAsync(cancellationToken);
        }
        return true;
    }
}
