using KeyPulse.Helpers;
using KeyPulse.Models;
using KeyPulse.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace KeyPulse.ViewModels
{
    public class DeviceListViewModel : ObservableObject
    {
        private USBMonitorService _usbMonitorService;
        private DataService _dataService;

        private ObservableCollection<USBDeviceInfo> _connectedDevices;
        public ObservableCollection<USBDeviceInfo> ConnectedDevices
        {
            get => _connectedDevices;
            set
            {
                _connectedDevices = value;
                OnPropertyChanged();
            }
        }

        public ICommand RenameDeviceCommand { get; }

        public DeviceListViewModel()
        {
            _dataService = new DataService();
            ConnectedDevices = [];
            _usbMonitorService = new USBMonitorService();
            _usbMonitorService.DeviceInserted += OnDeviceInserted;
            _usbMonitorService.DeviceRemoved += OnDeviceRemoved;

            var savedDevices = _dataService.GetAllDevices();
            foreach (var device in savedDevices)
            {
                ConnectedDevices.Add(device);
            }

            var currentDevices = _usbMonitorService.GetConnectedDevices();
            foreach (var device in currentDevices)
            {
                var existingDevice = FindDevice(device.DeviceID);
                if (existingDevice == null)
                {
                    ConnectedDevices.Add(device);
                    _dataService.SaveDevice(device);
                } else
                {
                    existingDevice.PnpDeviceID = device.PnpDeviceID;
                    existingDevice.DeviceName = device.DeviceName;
                    existingDevice.VID = device.VID;
                    existingDevice.PID = device.PID;
                }
            }

            RenameDeviceCommand = new RelayCommand(ExecuteRenameDevice, CanExecuteRenameDevice);
        }

        private void OnDeviceInserted(object sender, USBDeviceInfo device)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var existingDevice = FindDevice(device.DeviceID);
                if (existingDevice != null)
                {
                    ConnectedDevices.Add(device);
                    _dataService.SaveDevice(device);
                }
            });
        }

        private void OnDeviceRemoved(object sender, USBDeviceInfo device)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var deviceToRemove = FindDevice(device.DeviceID);
                if (deviceToRemove != null)
                {
                    ConnectedDevices.Remove(deviceToRemove);
                    //_dataService.DeleteDevice(device.DeviceID);
                }
            });
        }

        private USBDeviceInfo? FindDevice(string deviceId)
        {
            foreach (var device in ConnectedDevices)
            {
                if (device.DeviceID == deviceId)
                    return device;
            }
            return null;
        }

        private void ExecuteRenameDevice(object parameter)
        {
            if (parameter is USBDeviceInfo device)
            {
                string newName = PromptForDeviceName(device.DeviceName);
                if (!string.IsNullOrWhiteSpace(newName))
                {
                    device.DeviceName = newName;
                    _dataService.SaveDevice(device);
                    OnPropertyChanged(nameof(ConnectedDevices));
                }    
            }
        }

        private bool CanExecuteRenameDevice(object parameter)
        {
            return parameter is USBDeviceInfo;
        }

        private string PromptForDeviceName(string currentName)
        {
            return Microsoft.VisualBasic.Interaction.InputBox("Enter new name for the device:", "Rename Device", currentName);
        }
    }
}
