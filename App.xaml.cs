using KeyPulse.Data;
using KeyPulse.Services;
using KeyPulse.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;

namespace KeyPulse
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public static ServiceProvider ServiceProvider { get; private set; } = null!;

        protected override void OnStartup(StartupEventArgs e)
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            ServiceProvider = services.BuildServiceProvider();

            base.OnStartup(e);
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            services.AddDbContext<ApplicationDbContext>();
            services.AddScoped<DataService>();
            services.AddSingleton<USBMonitorService>();
            services.AddTransient<DeviceListViewModel>();
            services.AddTransient<ConnectionLogViewModel>();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            ServiceProvider?.Dispose();
            base.OnExit(e);
        }
    }
}
