using KeyPulse.Data;
using KeyPulse.Services;
using KeyPulse.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Windows;

namespace KeyPulse
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private USBMonitorService?  _usbMonitorService;
        public static ServiceProvider ServiceProvider { get; private set; } = null!;

        protected override void OnStartup(StartupEventArgs e)
        {
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
