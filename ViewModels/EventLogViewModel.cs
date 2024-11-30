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
    public class EventLogViewModel : ObservableObject
    {
        private readonly USBMonitorService _usbMonitorService;
        public ICollectionView EventLogCollection { get; }

        public EventLogViewModel(USBMonitorService usbMonitorService)
        {
            _usbMonitorService = usbMonitorService;
            EventLogCollection = CollectionViewSource.GetDefaultView(_usbMonitorService.DeviceEventList);
            EventLogCollection.SortDescriptions.Add(
                new SortDescription(nameof(DeviceEvent.Timestamp), ListSortDirection.Descending));
        }
    }
}
