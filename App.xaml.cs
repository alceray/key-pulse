using KeyPulse.Data;
using KeyPulse.Services;
using KeyPulse.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Windows;
using System.Reflection;
using System.Configuration;
using System.Windows.Forms;
using System.Drawing;
using System.ComponentModel;
using System.IO;

namespace KeyPulse
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private USBMonitorService?  _usbMonitorService;
        private static Mutex? _appMutex;
        private NotifyIcon? _trayIcon;
        private string? _appName;
        public static bool runInBackground { get; private set; }
        public static ServiceProvider ServiceProvider { get; private set; } = null!;

        protected override void OnStartup(StartupEventArgs e)
        {
            _appName = Assembly.GetExecutingAssembly().GetName().Name ?? "KeyPulse";
            _appMutex = new Mutex(true, _appName, out bool canCreateApp);
            if (!canCreateApp)
            {
                System.Windows.MessageBox.Show("The application is already running.", _appName, MessageBoxButton.OK, MessageBoxImage.Information);
                Environment.Exit(0);
            }

            var services = new ServiceCollection();
            ConfigureServices(services);
            ServiceProvider = services.BuildServiceProvider();
            _usbMonitorService = ServiceProvider.GetRequiredService<USBMonitorService>();

            runInBackground = bool.TryParse(ConfigurationManager.AppSettings["runInBackground"], out bool result) && result;
            if (runInBackground)
            {
                InitializeTrayIcon();
            }
            else
            {
                MainWindow = new MainWindow();
                MainWindow.Closing += MainWindow_Closing;
                MainWindow.Show();
            }

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _appMutex?.ReleaseMutex();
                _appMutex?.Dispose();
                _trayIcon?.Dispose();
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
            services.AddDbContext<ApplicationDbContext>();
            services.AddScoped<DataService>();
            services.AddSingleton<USBMonitorService>();
            services.AddTransient<DeviceListViewModel>();
            services.AddTransient<EventLogViewModel>();
        }

        private void InitializeTrayIcon()
        {
            _trayIcon = new NotifyIcon
            {
                Icon = new Icon(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "keyboard_mouse_icon.ico")),
                Visible = true,
                Text = _appName,
                ContextMenuStrip = new ContextMenuStrip()
            };
            _trayIcon.ContextMenuStrip.Items.Add("Open", null, (s, args) => ShowMainWindow());
            _trayIcon.ContextMenuStrip.Items.Add("Exit", null, (s, args) => Shutdown());
            _trayIcon.MouseClick += (s, args) =>
            {
                if (args.Button == MouseButtons.Left)
                {
                    ShowMainWindow();
                }
            };
        }

        private void ShowMainWindow()
        {
            if (MainWindow == null)
            {
                MainWindow = new MainWindow();
                MainWindow.Closing += MainWindow_Closing;
            }
            if (!MainWindow.IsVisible)
            {
                MainWindow.Show();
                MainWindow.WindowState = WindowState.Normal;
            }
            var activated = MainWindow.Activate();
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (runInBackground)
            {
                e.Cancel = true;
                MainWindow.Hide();
            }
        }
    }
}
