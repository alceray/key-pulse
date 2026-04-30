namespace KeyPulse.Models;

public class AppUserSettings
{
    public bool LaunchOnLogin { get; set; } = true;
    public bool IsFirstLaunch { get; set; } = true;
    public bool AutoInstallUpdates { get; set; } = true;
}
