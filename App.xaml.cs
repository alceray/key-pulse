using System.ComponentModel;
using System.IO;
using System.Windows;
using KeyPulse.Configuration;
using KeyPulse.Data;
using KeyPulse.Services;
using KeyPulse.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace KeyPulse;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private UsbMonitorService? _usbMonitorService;
    private RawInputService? _rawInputService;
    private UpdateService? _updateService;
    private TrayIconService? _trayIconService;
    private static Mutex? _appMutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateEventRegistration;
    private string? _appName;
    private AppSettingsService? _appSettingsService;
    private StartupRegistrationService? _startupRegistrationService;
    private static bool RunInBackground { get; set; }
    public static ServiceProvider ServiceProvider { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        _appName = AppConstants.App.DefaultName;
        var instanceId = GetInstanceId(_appName);
        ConfigureLogging();
        Log.Information("{AppName} startup initiated", _appName);

        // Attempt clean shutdown on unhandled exceptions (crashes).
        // Force-kills (IDE stop, TerminateProcess) cannot be caught - RecoverFromCrash() handles those.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception");
            _rawInputService?.Dispose();
            _usbMonitorService?.Dispose();
        };
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal(args.Exception, "Dispatcher unhandled exception");
            _rawInputService?.Dispose();
            _usbMonitorService?.Dispose();
        };

        _appMutex = new Mutex(true, instanceId, out var canCreateApp);
        if (!canCreateApp)
        {
            Log.Information("Secondary instance detected for {InstanceId}; signaling active instance", instanceId);
            if (!SignalExistingInstance(instanceId))
            {
                Log.Warning("Failed to signal existing instance; showing already-running message");
                System.Windows.MessageBox.Show(
                    "The application is already running.",
                    _appName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            }

            Environment.Exit(0);
        }

        InitializeActivationSignalListener(instanceId);

        var services = new ServiceCollection();
        ConfigureServices(services);
        ServiceProvider = services.BuildServiceProvider();

        _appSettingsService = ServiceProvider.GetRequiredService<AppSettingsService>();
        _startupRegistrationService = ServiceProvider.GetRequiredService<StartupRegistrationService>();
        _updateService = ServiceProvider.GetRequiredService<UpdateService>();
        _trayIconService = ServiceProvider.GetRequiredService<TrayIconService>();

        // Resolve startup mode with precedence: launch args > build default.
        RunInBackground = ResolveRunInBackground(e.Args);
        Log.Information("Startup mode resolved: RunInBackground={RunInBackground}", RunInBackground);

        SyncStartupRegistrationFromSettings();

        _usbMonitorService = ServiceProvider.GetRequiredService<UsbMonitorService>();

        // Show window / tray immediately so the UI appears while slow startup runs in the background.
        // First launch always shows the window, even in Release/tray mode.
        var settings = _appSettingsService.GetSettings();

        if (!RunInBackground || settings.IsFirstLaunch)
        {
            MainWindow = new MainWindow();
            MainWindow.Title = _appName;
            MainWindow.Closing += MainWindow_Closing;
            MainWindow.Show();

            // Mark first launch as done and save.
            if (settings.IsFirstLaunch)
            {
                settings.IsFirstLaunch = false;
                _appSettingsService.SaveSettings(settings);
            }
        }

        // Initialize tray if in background mode (either first launch or not).
        if (RunInBackground)
        {
            _trayIconService?.Initialize(ShowMainWindow, Shutdown);
        }

        // WMI device snapshot + watcher setup - awaited off the UI thread.
        // Failures here are logged but don't block; app continues in degraded mode.
        try
        {
            await _usbMonitorService.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UsbMonitorService startup failed");
            ShowStartupWarning(
                "Device monitoring failed to start completely. Some features may be unavailable. Check logs for details."
            );
        }

        _rawInputService = ServiceProvider.GetRequiredService<RawInputService>();

        // RawInputService startup handles its own exceptions internally.
        try
        {
            _rawInputService.Start();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RawInputService startup failed unexpectedly");
            ShowStartupWarning(
                "Activity tracking failed to start. The app will continue running but activity data may not be collected. Check logs for details."
            );
        }

        try
        {
            _updateService.Start();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "UpdateService startup failed unexpectedly");
            ShowStartupWarning(
                "Update checks failed to start. The app will continue running, and you can still try checking manually from Settings."
            );
        }

        Log.Information("{AppName} startup completed", _appName);
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("{AppName} shutdown initiated", _appName ?? AppConstants.App.DefaultName);
        try
        {
            _activateEventRegistration?.Unregister(null);
            _activateEvent?.Dispose();
            _appMutex?.ReleaseMutex();
            _appMutex?.Dispose();
            _trayIconService?.Dispose();
            _rawInputService?.Dispose();
            _updateService?.Dispose();
            _usbMonitorService?.Dispose();
            ServiceProvider.Dispose();
            Log.Information("{AppName} shutdown completed", _appName ?? AppConstants.App.DefaultName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during application shutdown");
        }
        finally
        {
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddDbContextFactory<ApplicationDbContext>();
        services.AddSingleton<DataService>();
        services.AddSingleton<AppSettingsService>();
        services.AddSingleton<LogAccessService>();
        services.AddSingleton<StartupRegistrationService>();
        services.AddSingleton<UsbMonitorService>();
        services.AddSingleton<RawInputService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<TrayIconService>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<DeviceListViewModel>();
        services.AddTransient<EventLogViewModel>();
        services.AddTransient<SettingsViewModel>();
    }

    private static bool ResolveRunInBackground(IEnumerable<string> args)
    {
        return ShouldForceTrayFromArgs(args) || IsProductionBuild();
    }

    private static bool IsProductionBuild()
    {
#if DEBUG
        return false;
#else
        return true;
#endif
    }

    private static bool ShouldForceTrayFromArgs(IEnumerable<string> args)
    {
        return args.Any(arg =>
            string.Equals(arg, AppConstants.App.StartupArgument, StringComparison.OrdinalIgnoreCase)
        );
    }

    private static string GetActivationEventName(string appName)
    {
        return $"{appName}{AppConstants.App.ActivationEventSuffix}";
    }

    private static string GetInstanceId(string appName)
    {
        return $"{appName}.{GetBuildModeName()}";
    }

    private static string GetBuildModeName()
    {
#if DEBUG
        return "Debug";
#else
        return "Release";
#endif
    }

    private static void ConfigureLogging()
    {
        try
        {
            var logDirectory = AppDataPaths.GetPath(AppConstants.Paths.LogsDirectoryName);
            Directory.CreateDirectory(logDirectory);

            var loggerConfiguration = new LoggerConfiguration().Enrich.FromLogContext().MinimumLevel.Debug();

            Log.Logger = loggerConfiguration
                .WriteTo.File(
                    Path.Combine(logDirectory, AppConstants.Paths.RollingLogFileTemplate),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: AppConstants.Logging.RetainedFileCountLimit,
                    shared: true
                )
                .CreateLogger();
        }
        catch
        {
            // Logging bootstrap must never block application startup.
        }
    }

    private void InitializeActivationSignalListener(string instanceId)
    {
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, GetActivationEventName(instanceId));
        _activateEventRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, _) => Dispatcher.BeginInvoke(new Action(ShowMainWindow)),
            null,
            Timeout.Infinite,
            false
        );
        Log.Debug(
            "Activation signal listener initialized for {ActivationEventName}",
            GetActivationEventName(instanceId)
        );
    }

    private static bool SignalExistingInstance(string instanceId)
    {
        try
        {
            using var activateEvent = EventWaitHandle.OpenExisting(GetActivationEventName(instanceId));
            return activateEvent.Set();
        }
        catch
        {
            Log.Warning("Activation signal event was not available for {InstanceId}", instanceId);
            return false;
        }
    }

    private void SyncStartupRegistrationFromSettings()
    {
        if (_appSettingsService == null || _startupRegistrationService == null)
            return;

        try
        {
            var settings = _appSettingsService.GetSettings();
            if (settings.LaunchOnLogin)
                _startupRegistrationService.Enable();
            else
                _startupRegistrationService.Disable();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to synchronize startup registration from settings");
            ShowStartupWarning("Launch on Login could not be synchronized. Check logs for details.");
        }
    }

    private void ShowStartupWarning(string message)
    {
        try
        {
            if (RunInBackground && _trayIconService != null)
                _trayIconService.ShowWarning(
                    "Startup Warning",
                    message,
                    AppConstants.Logging.StartupWarningBalloonTimeoutMs
                );
            else if (!RunInBackground && MainWindow != null)
                MainWindow.Dispatcher.BeginInvoke(() =>
                {
                    System.Windows.MessageBox.Show(message, _appName, MessageBoxButton.OK, MessageBoxImage.Warning);
                });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to show startup warning");
        }
    }

    private void ShowMainWindow()
    {
        if (MainWindow == null)
        {
            MainWindow = new MainWindow();
            MainWindow.Title = _appName;
            MainWindow.Closing += MainWindow_Closing;
        }

        if (MainWindow.WindowState == WindowState.Minimized)
            MainWindow.WindowState = WindowState.Normal;

        if (!MainWindow.IsVisible)
            MainWindow.Show();

        MainWindow.Topmost = true;
        MainWindow.Topmost = false;
        MainWindow.Activate();
        MainWindow.Focus();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (RunInBackground)
        {
            e.Cancel = true;
            MainWindow.Hide();
        }
    }
}
