using System.Diagnostics;
using System.IO;
using KeyPulse.Configuration;

namespace KeyPulse.Services;

public class LogAccessService
{
    private readonly string _logDirectory;

    public LogAccessService()
    {
        _logDirectory = AppDataPaths.GetPath(AppConstants.Paths.LogsDirectoryName);
        Directory.CreateDirectory(_logDirectory);
    }

    public string LogDirectory => _logDirectory;

    public IReadOnlyList<string> GetLogFiles()
    {
        if (!Directory.Exists(_logDirectory))
            return [];

        return Directory
            .GetFiles(_logDirectory, AppConstants.Paths.LogFilePattern, SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string ReadLogContent(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return string.Empty;

        var filePath = Path.Combine(_logDirectory, fileName);
        if (!File.Exists(filePath))
            return string.Empty;

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void OpenLogsFolder()
    {
        Directory.CreateDirectory(_logDirectory);
        Process.Start(new ProcessStartInfo { FileName = _logDirectory, UseShellExecute = true });
    }
}
