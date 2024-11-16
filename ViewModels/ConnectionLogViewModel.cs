using KeyPulse.Helpers;
using KeyPulse.Models;
using KeyPulse.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;

namespace KeyPulse.ViewModels
{
    public class ConnectionLogViewModel : ObservableObject, IDisposable
    {
        private readonly USBMonitorService _usbMonitorService;
        public ICollectionView ConnectionLogCollView { get; }

        public ConnectionLogViewModel(USBMonitorService usbMonitorService)
        {
            _usbMonitorService = usbMonitorService;
            ConnectionLogCollView = CollectionViewSource.GetDefaultView(_usbMonitorService.ConnectionEvents);
            ConnectionLogCollView.SortDescriptions.Add(
                new SortDescription(nameof(Connection.ConnectedAt), ListSortDirection.Descending));
        }

        public void Dispose()
        {
            _usbMonitorService.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
