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
        public ICollectionView DeviceListCollection { get; }

        public ICommand RenameDeviceCommand { get; }
        public string DeviceNameHeader => $"Devices ({DeviceListCollection.Cast<object>().Count()})";

        public bool ShowAllDevices
        {
            get => _showAllDevices;
            set
            {
                if (_showAllDevices != value)
                {
                    _showAllDevices = value;
                    Application.Current.Dispatcher.BeginInvoke(() => DeviceListCollection.Refresh());
                    OnPropertyChanged(nameof(ShowAllDevices));
                    OnPropertyChanged(nameof(DeviceNameHeader));
                }
            }
        }
        private bool _showAllDevices = false;

        public DeviceListViewModel(USBMonitorService usbMonitorService)
        {
            _usbMonitorService = usbMonitorService;

            DeviceListCollection = CollectionViewSource.GetDefaultView(_usbMonitorService.DeviceList);
            DeviceListCollection.Filter = device => ShowAllDevices || ((DeviceInfo)device).IsActive;

            foreach (var device in _usbMonitorService.DeviceList)
                device.PropertyChanged += Device_PropertyChanged;

            _usbMonitorService.DeviceList.CollectionChanged += DeviceList_CollectionChanged;

            RenameDeviceCommand = new RelayCommand(ExecuteRenameDevice, CanExecuteRenameDevice);
        }

        private void Device_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DeviceInfo.IsActive))
            {
                Application.Current.Dispatcher.BeginInvoke(() => DeviceListCollection.Refresh());
                OnPropertyChanged(nameof(DeviceNameHeader));
            }
        }

        private void DeviceList_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (DeviceInfo device in e.NewItems)
                    device.PropertyChanged += Device_PropertyChanged;
            }
            if (e.OldItems != null)
            {
                foreach (DeviceInfo device in e.OldItems)
                    device.PropertyChanged -= Device_PropertyChanged;
            }

            Application.Current.Dispatcher.BeginInvoke(() => DeviceListCollection.Refresh());
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
            foreach (var device in _usbMonitorService.DeviceList)
                device.PropertyChanged -= Device_PropertyChanged;
            
            _usbMonitorService.DeviceList.CollectionChanged -= DeviceList_CollectionChanged;
            GC.SuppressFinalize(this);
        }
    }
}
