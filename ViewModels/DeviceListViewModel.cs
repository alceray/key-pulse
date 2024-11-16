using KeyPulse.Helpers;
using KeyPulse.Models;
using KeyPulse.Services;
using Microsoft.VisualBasic;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace KeyPulse.ViewModels
{
    public class DeviceListViewModel : ObservableObject, IDisposable
    {
        private readonly USBMonitorService _usbMonitorService;
        public ICollectionView AllDevicesCollView { get; }

        public ICommand RenameDeviceCommand { get; }
        public string DeviceNameHeader => $"Devices ({AllDevicesCollView.Cast<object>().Count()})";

        public bool ShowAllDevices
        {
            get => _showAllDevices;
            set
            {
                if (_showAllDevices != value)
                {
                    _showAllDevices = value;
                    Application.Current.Dispatcher.Invoke(() => AllDevicesCollView.Refresh());
                    OnPropertyChanged(nameof(ShowAllDevices));
                    OnPropertyChanged(nameof(DeviceNameHeader));
                }
            }
        }
        private bool _showAllDevices = false;

        public DeviceListViewModel(USBMonitorService usbMonitorService)
        {
            _usbMonitorService = usbMonitorService;

            AllDevicesCollView = CollectionViewSource.GetDefaultView(_usbMonitorService.AllDevices);
            AllDevicesCollView.Filter = device => ShowAllDevices || ((DeviceInfo)device).IsConnected;

            foreach (var device in _usbMonitorService.AllDevices)
                device.PropertyChanged += Device_PropertyChanged;

            _usbMonitorService.AllDevices.CollectionChanged += ConnectedDevices_CollectionChanged;

            RenameDeviceCommand = new RelayCommand(ExecuteRenameDevice, CanExecuteRenameDevice);
        }

        private void Device_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DeviceInfo.IsConnected))
            {
                Application.Current.Dispatcher.Invoke(() => AllDevicesCollView.Refresh());
                OnPropertyChanged(nameof(DeviceNameHeader));
            }
        }

        private void ConnectedDevices_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (DeviceInfo device in e.NewItems)
                    device.PropertyChanged += Device_PropertyChanged;
            }

            Application.Current.Dispatcher.Invoke(() => AllDevicesCollView.Refresh());
            OnPropertyChanged(nameof(DeviceNameHeader));
        }

        private void ExecuteRenameDevice(object parameter)
        {
            if (parameter is DeviceInfo device)
            {
                string newName = PromptForDeviceName(device.DeviceName);
                if (!string.IsNullOrWhiteSpace(newName))
                {
                    device.DeviceName = newName;
                    OnPropertyChanged(nameof(device.DeviceName));
                }    
            }
        }

        private bool CanExecuteRenameDevice(object parameter) => parameter is DeviceInfo;

        private static string PromptForDeviceName(string currentName) => 
            Interaction.InputBox("Enter new name for the device:", "Rename Device", currentName);

        public void Dispose()
        {
            foreach (var device in _usbMonitorService.AllDevices)
                device.PropertyChanged -= Device_PropertyChanged;
            
            _usbMonitorService.AllDevices.CollectionChanged -= ConnectedDevices_CollectionChanged;
            _usbMonitorService.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
