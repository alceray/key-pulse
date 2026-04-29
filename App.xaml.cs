using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;
using KeyPulse.Configuration;
using KeyPulse.Data;
using KeyPulse.Models;
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
    private static Mutex? _appMutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateEventRegistration;
    private NotifyIcon? _trayIcon;
    private ToolStripMenuItem? _launchOnLoginMenuItem;
    private string? _appName;
    private AppSettingsService? _appSettingsService;
    private LogAccessService? _logAccessService;
    private StartupRegistrationService? _startupRegistrationService;
    private static bool RunInBackground { get; set; }
    public static ServiceProvider ServiceProvider { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        _appName = Assembly.GetExecutingAssembly().GetName().Name ?? AppConstants.App.DefaultName;
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

        _appMutex = new Mutex(true, _appName, out var canCreateApp);
        if (!canCreateApp)
        {
            Log.Information("Secondary instance detected; signaling active instance");
            if (!SignalExistingInstance(_appName))
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

        InitializeActivationSignalListener();

        var services = new ServiceCollection();
        ConfigureServices(services);
        ServiceProvider = services.BuildServiceProvider();

        _appSettingsService = ServiceProvider.GetRequiredService<AppSettingsService>();
        _logAccessService = ServiceProvider.GetRequiredService<LogAccessService>();
        _startupRegistrationService = ServiceProvider.GetRequiredService<StartupRegistrationService>();
        _appSettingsService.SettingsChanged += OnAppSettingsChanged;

        // Resolve startup mode with precedence: launch args > build default.
        RunInBackground = ResolveRunInBackground(e.Args);
        Log.Information("Startup mode resolved: RunInBackground={RunInBackground}", RunInBackground);

        SyncStartupRegistrationFromSettings();

        _usbMonitorService = ServiceProvider.GetRequiredService<UsbMonitorService>();

        // Show window / tray immediately so the UI appears while slow startup runs in the background.
        if (RunInBackground)
        {
            InitializeTrayIcon();
        }
        else
        {
            MainWindow = new MainWindow();
            MainWindow.Closing += MainWindow_Closing;
            MainWindow.Show();
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
            if (_appSettingsService != null)
                _appSettingsService.SettingsChanged -= OnAppSettingsChanged;
            _appMutex?.ReleaseMutex();
            _appMutex?.Dispose();
            _trayIcon?.Dispose();
            _rawInputService?.Dispose();
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

    private static void ConfigureLogging()
    {
        try
        {
            var logDirectory = AppDataPaths.GetPath(AppConstants.Paths.LogsDirectoryName);
            Directory.CreateDirectory(logDirectory);

            var loggerConfiguration = new LoggerConfiguration().Enrich.FromLogContext();
#if DEBUG
            loggerConfiguration = loggerConfiguration.MinimumLevel.Debug();
#else
            loggerConfiguration = loggerConfiguration.MinimumLevel.Information();
#endif

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

    private void InitializeActivationSignalListener()
    {
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, GetActivationEventName(_appName!));
        _activateEventRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, _) => Dispatcher.BeginInvoke(new Action(ShowMainWindow)),
            null,
            Timeout.Infinite,
            false
        );
        Log.Debug(
            "Activation signal listener initialized for {ActivationEventName}",
            GetActivationEventName(_appName!)
        );
    }

    private static bool SignalExistingInstance(string appName)
    {
        try
        {
            using var activateEvent = EventWaitHandle.OpenExisting(GetActivationEventName(appName));
            return activateEvent.Set();
        }
        catch
        {
            Log.Warning("Activation signal event was not available for {AppName}", appName);
            return false;
        }
    }

    private void InitializeTrayIcon()
    {
        var settings = _appSettingsService?.GetSettings() ?? new AppUserSettings();

        _trayIcon = new NotifyIcon
        {
            Icon = new Icon(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "keyboard_mouse_icon.ico")),
            Visible = true,
            Text = _appName,
            ContextMenuStrip = new ContextMenuStrip(),
        };
        _trayIcon.ContextMenuStrip.Items.Add("Open", null, (_, _) => ShowMainWindow());

        _launchOnLoginMenuItem = new ToolStripMenuItem("Launch on Login")
        {
            CheckOnClick = true,
            Checked = settings.LaunchOnLogin,
        };
        _launchOnLoginMenuItem.Click += (_, _) => ToggleLaunchOnLogin();
        _trayIcon.ContextMenuStrip.Items.Add(_launchOnLoginMenuItem);

        _trayIcon.ContextMenuStrip.Items.Add("Exit", null, (_, _) => Shutdown());
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
                ShowMainWindow();
        };

        Log.Information("Tray icon initialized");
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

            ApplySettingsToTrayMenu(settings);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to synchronize startup registration from settings");
            ShowStartupWarning("Launch on Login could not be synchronized. Check logs for details.");
        }
    }

    private void ToggleLaunchOnLogin()
    {
        if (_appSettingsService == null || _startupRegistrationService == null || _launchOnLoginMenuItem == null)
            return;

        var enabled = _launchOnLoginMenuItem.Checked;

        try
        {
            var settings = _appSettingsService.GetSettings();
            settings.LaunchOnLogin = enabled;
            _appSettingsService.SaveSettings(settings);

            if (enabled)
                _startupRegistrationService.Enable();
            else
                _startupRegistrationService.Disable();

            ApplySettingsToTrayMenu(settings);

            Log.Information("Launch on Login updated: Enabled={Enabled}", enabled);
        }
        catch (Exception ex)
        {
            _launchOnLoginMenuItem.Checked = !enabled;
            Log.Error(ex, "Failed to update Launch on Login setting");
            ShowStartupWarning("Could not update Launch on Login. Check logs for details.");
        }
    }

    private void OnAppSettingsChanged(AppUserSettings settings)
    {
        ApplySettingsToTrayMenu(settings);
    }

    private void ApplySettingsToTrayMenu(AppUserSettings settings)
    {
        if (_launchOnLoginMenuItem == null)
            return;

        Dispatcher.BeginInvoke(() => _launchOnLoginMenuItem.Checked = settings.LaunchOnLogin);
    }

    private void ShowStartupWarning(string message)
    {
        try
        {
            if (RunInBackground && _trayIcon != null)
                _trayIcon.ShowBalloonTip(
                    AppConstants.Logging.StartupWarningBalloonTimeoutMs,
                    "Startup Warning",
                    message,
                    ToolTipIcon.Warning
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
