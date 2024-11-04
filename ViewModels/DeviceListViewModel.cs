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
    public class DeviceListViewModel : ObservableObject
    {
        private readonly USBMonitorService _usbMonitorService;

        public ICollectionView ConnectedDevicesView
        {
            get => _connectedDevicesView;
            set
            {
                _connectedDevicesView = value;
                Application.Current.Dispatcher.Invoke(() => OnPropertyChanged(nameof(ConnectedDevicesView)));
            }
        }
        private ICollectionView _connectedDevicesView = CollectionViewSource.GetDefaultView(new ObservableCollection<USBDeviceInfo>());

        public bool ShowAllDevices
        {
            get => _showAllDevices;
            set
            {
                if (_showAllDevices != value)
                {
                    _showAllDevices = value;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        ConnectedDevicesView.Refresh();
                        OnPropertyChanged(nameof(ShowAllDevices));
                        OnPropertyChanged(nameof(DeviceCount));
                        OnPropertyChanged(nameof(DeviceNameHeader));
                    });
                }
            }
        }
        private bool _showAllDevices = false;

        public int DeviceCount => ConnectedDevicesView.Cast<object>().Count();
        public string DeviceNameHeader => $"Devices ({DeviceCount})";
        public ICommand RenameDeviceCommand { get; }

        public DeviceListViewModel()
        {
            _usbMonitorService = new USBMonitorService();

            ConnectedDevicesView = CollectionViewSource.GetDefaultView(_usbMonitorService.ConnectedDevices);
            ConnectedDevicesView.Filter = device =>
            {
                var usbDevice = (USBDeviceInfo)device;
                return ShowAllDevices || usbDevice.IsConnected;
            };

            foreach (var device in _usbMonitorService.ConnectedDevices)
            {
                device.PropertyChanged += Device_PropertyChanged;
            }
            _usbMonitorService.ConnectedDevices.CollectionChanged += ConnectedDevices_CollectionChanged;

            RenameDeviceCommand = new RelayCommand(ExecuteRenameDevice, CanExecuteRenameDevice);
        }

        private void Device_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(USBDeviceInfo.IsConnected))
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ConnectedDevicesView.Refresh();
                    OnPropertyChanged(nameof(DeviceCount));
                    OnPropertyChanged(nameof(DeviceNameHeader));
                });
            } 
        }

        private void ConnectedDevices_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (USBDeviceInfo device in e.NewItems)
                {
                    device.PropertyChanged += Device_PropertyChanged;
                }
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                ConnectedDevicesView.Refresh();
                OnPropertyChanged(nameof(DeviceCount));
                OnPropertyChanged(nameof(DeviceNameHeader));
            });
        }

        private void ExecuteRenameDevice(object parameter)
        {
            if (parameter is USBDeviceInfo device)
            {
                string newName = PromptForDeviceName(device.DeviceName);
                if (!string.IsNullOrWhiteSpace(newName))
                {
                    device.DeviceName = newName;
                    Application.Current.Dispatcher.Invoke(() => OnPropertyChanged(nameof(device.DeviceName)));
                }    
            }
        }

        private bool CanExecuteRenameDevice(object parameter)
        {
            return parameter is USBDeviceInfo;
        }

        private string PromptForDeviceName(string currentName)
        {
            return Interaction.InputBox("Enter new name for the device:", "Rename Device", currentName);
        }
    }
}
