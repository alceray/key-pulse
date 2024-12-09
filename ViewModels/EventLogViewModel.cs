using KeyPulse.Helpers;
using KeyPulse.Models;
using KeyPulse.Services;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;

namespace KeyPulse.ViewModels
{
    public class EventLogViewModel : ObservableObject, IDisposable
    {
        private readonly USBMonitorService _usbMonitorService;
        private readonly List<EventTypes> _hiddenEvents =
        [
            EventTypes.ConnectionEnded,
            EventTypes.ConnectionStarted,
        ];
        public ICollectionView EventLogCollection { get; }

        public EventLogViewModel(USBMonitorService usbMonitorService)
        {
            _usbMonitorService = usbMonitorService;
            EventLogCollection = CollectionViewSource.GetDefaultView(_usbMonitorService.DeviceEventList);
            EventLogCollection.Filter = de => !_hiddenEvents.Contains(((DeviceEvent)de).EventType);
            EventLogCollection.SortDescriptions.Add(
                new SortDescription(nameof(DeviceEvent.Timestamp), ListSortDirection.Descending));
            _usbMonitorService.DeviceEventList.CollectionChanged += EventLog_CollectionChanged;
        }

        private void EventLog_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            Application.Current.Dispatcher.BeginInvoke(() => EventLogCollection.Refresh());
        }

        public void Dispose()
        {
            _usbMonitorService.DeviceEventList.CollectionChanged -= EventLog_CollectionChanged;
            GC.SuppressFinalize(this);
        }
    }
}
