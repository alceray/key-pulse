using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using KeyPulse.Helpers;
using KeyPulse.Models;
using KeyPulse.Services;
using Microsoft.VisualBasic;

namespace KeyPulse.ViewModels;

public class DeviceListViewModel : ObservableObject, IDisposable
{
    private readonly UsbMonitorService _usbMonitorService;
    private readonly RawInputService _rawInputService;
    private readonly DispatcherTimer _timer;
    private bool _showAllDevices;
    private string _currentSessionTime = "00:00:00";

    public ICollectionView DeviceListCollection { get; }
    public ICommand RenameDeviceCommand { get; }

    public string DeviceTitleWithCount => $"Devices ({DeviceListCollection.Cast<object>().Count()})";

    public bool ShowAllDevices
    {
        get => _showAllDevices;
        set
        {
            if (_showAllDevices != value)
            {
                _showAllDevices = value;
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    DeviceListCollection.Refresh();
                    OnPropertyChanged(nameof(DeviceTitleWithCount));
                });
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Formatted current app session time (HH:mm:ss).
    /// </summary>
    public string CurrentSessionTime
    {
        get => _currentSessionTime;
        set
        {
            if (_currentSessionTime != value)
            {
                _currentSessionTime = value;
                OnPropertyChanged(nameof(CurrentSessionTime));
            }
        }
    }

    public DeviceListViewModel(UsbMonitorService usbMonitorService, RawInputService rawInputService)
    {
        _usbMonitorService = usbMonitorService;
        _rawInputService = rawInputService;

        DeviceListCollection = CollectionViewSource.GetDefaultView(_usbMonitorService.DeviceList);
        DeviceListCollection.Filter = device => ShowAllDevices || ((Device)device).IsConnected;

        foreach (var device in _usbMonitorService.DeviceList)
            device.PropertyChanged += Device_PropertyChanged;

        _usbMonitorService.DeviceList.CollectionChanged += DeviceList_CollectionChanged;
        _rawInputService.ActivityStateChanged += OnActivityStateChanged;

        RenameDeviceCommand = new RelayCommand(ExecuteRenameDevice, CanExecuteRenameDevice);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (s, e) =>
        {
            // Update app session time from the AppStarted event timestamp.
            var elapsed = DateTime.Now - _usbMonitorService.AppSessionStartedAt;
            CurrentSessionTime = $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";

            // Refresh in-memory dynamic device display values.
            foreach (var device in _usbMonitorService.DeviceList)
                device.RefreshDynamicProperties();
        };
        _timer.Start();
    }

    private void Device_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Device.IsConnected))
        {
            if (sender is Device device && !device.IsConnected)
            {
                _rawInputService.ClearDeviceHoldState(device.DeviceId);
                device.SetActivityState(false);
            }

            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                DeviceListCollection.Refresh();
                OnPropertyChanged(nameof(DeviceTitleWithCount));
            });
        }
    }

    private void OnActivityStateChanged(string deviceId, bool isActive)
    {
        var device = _usbMonitorService.DeviceList.FirstOrDefault(d => d.DeviceId == deviceId);
        if (device != null)
            Application.Current.Dispatcher.BeginInvoke(() => device.SetActivityState(isActive));
    }

    private void DeviceList_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (Device device in e.NewItems)
                device.PropertyChanged += Device_PropertyChanged;

        if (e.OldItems != null)
            foreach (Device device in e.OldItems)
                device.PropertyChanged -= Device_PropertyChanged;

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            DeviceListCollection.Refresh();
            OnPropertyChanged(nameof(DeviceTitleWithCount));
        });
    }

    private void ExecuteRenameDevice(object parameter)
    {
        if (parameter is Device device)
        {
            var newName = PromptForDeviceName(device.DeviceName);
            if (!string.IsNullOrWhiteSpace(newName))
            {
                device.DeviceName = newName;
            }
        }
    }

    private bool CanExecuteRenameDevice(object parameter)
    {
        return parameter is Device;
    }

    private static string PromptForDeviceName(string currentName)
    {
        return Interaction.InputBox("Enter new name for the device:", "Rename Device", currentName);
    }

    public void Dispose()
    {
        foreach (var device in _usbMonitorService.DeviceList)
            device.PropertyChanged -= Device_PropertyChanged;

        _usbMonitorService.DeviceList.CollectionChanged -= DeviceList_CollectionChanged;
        _rawInputService.ActivityStateChanged -= OnActivityStateChanged;

        _timer.Stop();
        GC.SuppressFinalize(this);
    }
}
