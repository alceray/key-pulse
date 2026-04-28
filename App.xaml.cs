using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;
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
    private static Mutex? _appMutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateEventRegistration;
    private NotifyIcon? _trayIcon;
    private string? _appName;
    private static bool RunInBackground { get; set; }
    public static ServiceProvider ServiceProvider { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        _appName = Assembly.GetExecutingAssembly().GetName().Name ?? "KeyPulse";
        ConfigureLogging(_appName);
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

        // Resolve startup mode before services start so the value is stable.
        // Default: Debug => foreground window, Release => tray/background.
        // Launch args can force tray startup for packaging/startup-entry scenarios.
        RunInBackground = ResolveRunInBackground(e.Args);
        Log.Information("Startup mode resolved: RunInBackground={RunInBackground}", RunInBackground);

        var services = new ServiceCollection();
        ConfigureServices(services);
        ServiceProvider = services.BuildServiceProvider();

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
        await _usbMonitorService.StartAsync();

        _rawInputService = ServiceProvider.GetRequiredService<RawInputService>();
        _rawInputService.Start();

        Log.Information("{AppName} startup completed", _appName);
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("{AppName} shutdown initiated", _appName ?? "KeyPulse");
        try
        {
            _activateEventRegistration?.Unregister(null);
            _activateEvent?.Dispose();
            _appMutex?.ReleaseMutex();
            _appMutex?.Dispose();
            _trayIcon?.Dispose();
            _rawInputService?.Dispose();
            _usbMonitorService?.Dispose();
            ServiceProvider.Dispose();
            Log.Information("{AppName} shutdown completed", _appName ?? "KeyPulse");
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
        services.AddSingleton<UsbMonitorService>();
        services.AddSingleton<RawInputService>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<DeviceListViewModel>();
        services.AddTransient<EventLogViewModel>();
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
            string.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase)
        );
    }

    private static string GetActivationEventName(string appName)
    {
        return $"{appName}.ACTIVATE";
    }

    private static void ConfigureLogging(string appName)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var logDirectory = Path.Combine(appData, appName, "Logs");
            Directory.CreateDirectory(logDirectory);

            var loggerConfiguration = new LoggerConfiguration().Enrich.FromLogContext();
#if DEBUG
            loggerConfiguration = loggerConfiguration.MinimumLevel.Debug();
#else
            loggerConfiguration = loggerConfiguration.MinimumLevel.Information();
#endif

            Log.Logger = loggerConfiguration
                .WriteTo.File(
                    Path.Combine(logDirectory, "keypulse-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
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
        _trayIcon = new NotifyIcon
        {
            Icon = new Icon(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "keyboard_mouse_icon.ico")),
            Visible = true,
            Text = _appName,
            ContextMenuStrip = new ContextMenuStrip(),
        };
        _trayIcon.ContextMenuStrip.Items.Add("Open", null, (_, _) => ShowMainWindow());
        _trayIcon.ContextMenuStrip.Items.Add("Exit", null, (_, _) => Shutdown());
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
                ShowMainWindow();
        };

        Log.Information("Tray icon initialized");
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
