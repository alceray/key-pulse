using KeyPulse.Data;
using KeyPulse.Services;
using KeyPulse.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Windows;
using System.Reflection;

namespace KeyPulse
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private USBMonitorService?  _usbMonitorService;
        private static Mutex? _appMutex;
        public static ServiceProvider ServiceProvider { get; private set; } = null!;

        protected override void OnStartup(StartupEventArgs e)
        {
            var appName = Assembly.GetExecutingAssembly().GetName().Name ?? "KeyPulse";
            _appMutex = new Mutex(true, appName, out bool canCreateApp);
            if (!canCreateApp)
            {
                MessageBox.Show("The application is already running.", appName, MessageBoxButton.OK, MessageBoxImage.Information);
                Environment.Exit(0);
            }

            var services = new ServiceCollection();
            ConfigureServices(services);
            ServiceProvider = services.BuildServiceProvider();
            _usbMonitorService = ServiceProvider.GetRequiredService<USBMonitorService>();

            base.OnStartup(e);
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            services.AddDbContext<ApplicationDbContext>();
            services.AddScoped<DataService>();
            services.AddSingleton<USBMonitorService>();
            services.AddTransient<DeviceListViewModel>();
            services.AddTransient<EventLogViewModel>();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _appMutex?.ReleaseMutex();
                _appMutex?.Dispose();
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
    }
}
