using KeyPulse.Services;
using System.Windows;

namespace KeyPulse
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private USBMonitorService? usbMonitorService;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            usbMonitorService = new USBMonitorService();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            usbMonitorService?.Dispose();
            base.OnExit(e);
        }
    }

}
