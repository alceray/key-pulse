using System.IO;

namespace KeyPulse.Configuration;

public static class AppDataPaths
{
    public static string GetAppDataDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appDirectory = Path.Combine(appData, AppConstants.App.ProductName);
#if DEBUG
        appDirectory = Path.Combine(appDirectory, AppConstants.Paths.TestDataDirectoryName);
#endif
        Directory.CreateDirectory(appDirectory);
        return appDirectory;
    }

    public static string GetPath(params string[] relativeSegments)
    {
        var path = GetAppDataDirectory();
        foreach (var segment in relativeSegments)
            path = Path.Combine(path, segment);

        return path;
    }
}
