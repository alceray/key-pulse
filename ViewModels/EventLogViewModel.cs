using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using KeyPulse.Helpers;
using KeyPulse.Models;
using KeyPulse.Services;

namespace KeyPulse.ViewModels;

public class EventLogViewModel : ObservableObject, IDisposable
{
    private readonly UsbMonitorService _usbMonitorService;

    private readonly List<EventTypes> _hiddenEvents =
    [
        EventTypes.ConnectionEnded,
        EventTypes.ConnectionStarted,
        EventTypes.AppStarted,
        EventTypes.AppEnded,
    ];

    public ICollectionView EventLogCollection { get; }

    public EventLogViewModel(UsbMonitorService usbMonitorService)
    {
        _usbMonitorService = usbMonitorService;
        EventLogCollection = CollectionViewSource.GetDefaultView(_usbMonitorService.DeviceEventList);
        EventLogCollection.Filter = de => !_hiddenEvents.Contains(((DeviceEvent)de).EventType);
        EventLogCollection.SortDescriptions.Add(
            new SortDescription(nameof(DeviceEvent.EventTime), ListSortDirection.Descending)
        );
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
