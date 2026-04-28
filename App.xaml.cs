using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;
using KeyPulse.Data;
using KeyPulse.Services;
using KeyPulse.ViewModels;
using Microsoft.Extensions.DependencyInjection;

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
        // Attempt clean shutdown on unhandled exceptions (crashes).
        // Force-kills (IDE stop, TerminateProcess) cannot be caught — RecoverFromCrash() handles those.
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            Debug.WriteLine($"Unhandled exception: {args.ExceptionObject}");
            _rawInputService?.Dispose();
            _usbMonitorService?.Dispose();
        };
        DispatcherUnhandledException += (s, args) =>
        {
            Debug.WriteLine($"Dispatcher unhandled exception: {args.Exception}");
            _rawInputService?.Dispose();
            _usbMonitorService?.Dispose();
        };

        _appName = Assembly.GetExecutingAssembly().GetName().Name ?? "KeyPulse";
        _appMutex = new Mutex(true, _appName, out var canCreateApp);
        if (!canCreateApp)
        {
            if (!SignalExistingInstance(_appName))
                System.Windows.MessageBox.Show(
                    "The application is already running.",
                    _appName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );

            Environment.Exit(0);
        }

        InitializeActivationSignalListener();

        // Resolve startup mode before services start so the value is stable.
        // Default: Debug => foreground window, Release => tray/background.
        // Launch args can force tray startup for packaging/startup-entry scenarios.
        RunInBackground = ResolveRunInBackground(e.Args);

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

        // WMI device snapshot + watcher setup — awaited off the UI thread.
        await _usbMonitorService.StartAsync();

        _rawInputService = ServiceProvider.GetRequiredService<RawInputService>();
        _rawInputService.Start();

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _activateEventRegistration?.Unregister(null);
            _activateEvent?.Dispose();
            _appMutex?.ReleaseMutex();
            _appMutex?.Dispose();
            _trayIcon?.Dispose();
            _rawInputService?.Dispose();
            _usbMonitorService?.Dispose();
            ServiceProvider?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error during application shutdown: {ex.Message}");
        }
        finally
        {
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
        _trayIcon.ContextMenuStrip.Items.Add("Open", null, (s, args) => ShowMainWindow());
        _trayIcon.ContextMenuStrip.Items.Add("Exit", null, (s, args) => Shutdown());
        _trayIcon.MouseClick += (s, args) =>
        {
            if (args.Button == MouseButtons.Left)
                ShowMainWindow();
        };
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
