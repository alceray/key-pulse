using KeyPulse.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading.Tasks;

namespace KeyPulse.Services
{
    public class USBMonitorService
    {
        public event EventHandler<USBDeviceInfo> DeviceInserted;
        public event EventHandler<USBDeviceInfo> DeviceRemoved;
        private ManagementEventWatcher insertWatcher;
        private ManagementEventWatcher removeWatcher;
        private readonly List<USBDeviceInfo> _connectedDevices;

        public USBMonitorService() 
        {
            _connectedDevices = GetConnectedDevicesFromSystem();
            StartMonitoring();
        }

        public void StartMonitoring()
        {
            WqlEventQuery insertQuery = new WqlEventQuery("SELECT * FROM __InstanceCreationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_USBControllerDevice'");
            insertWatcher = new ManagementEventWatcher(insertQuery);
            insertWatcher.EventArrived += DeviceInsertedEvent;
            insertWatcher.Start();

            WqlEventQuery removeQuery = new WqlEventQuery("SELECT * FROM __InstanceDeletionEVent WITHIN 2 WHERE TargetInstance ISA 'Win32_USBControllerDevice'");
            removeWatcher = new ManagementEventWatcher(removeQuery);
            removeWatcher.EventArrived += DeviceRemovedEvent;
            removeWatcher.Start();
        }

        private void DeviceInsertedEvent(object sender, EventArrivedEventArgs e)
        {
            USBDeviceInfo device = GetDeviceInfo((ManagementBaseObject)e.NewEvent["TargetInstance"]);
            _connectedDevices.Add(device);
            DeviceInserted.Invoke(this, device);
        }

        private void DeviceRemovedEvent(object sender, EventArrivedEventArgs e)
        {
            USBDeviceInfo device = GetDeviceInfo((ManagementBaseObject)e.NewEvent["TargetInstance"]);
            var deviceToRemove = _connectedDevices.Remove(device);
            DeviceRemoved.Invoke(this, device);
        }

        private USBDeviceInfo? GetDeviceInfo(ManagementBaseObject obj) 
        {
            try
            {
                Console.WriteLine("Properties of ManagementBaseObject:");
                foreach (var property in obj.Properties)
                {
                    string name = property.Name;
                    object value = property.Value;
                    Console.WriteLine($"Name: {name}, Value: {value}");
                }
                string deviceId = obj["DeviceID"]?.ToString();
                string pnpDeviceId = obj["PNPDeviceID"]?.ToString();
                string deviceName = obj["Name"]?.ToString();
                string vid = GetValueFromDeviceId(pnpDeviceId, "VID_");
                string pid = GetValueFromDeviceId(pnpDeviceId, "PID_");
                if (vid == "" || pid == "")
                {
                    Console.WriteLine("Error: VID or PID was empty");
                    return null;
                }
                return new USBDeviceInfo
                {
                    DeviceID = deviceId,
                    PnpDeviceID = pnpDeviceId,
                    VID = vid,
                    PID = pid,
                    DeviceName = deviceName
                };
            }
            catch (ManagementException ex)
            {
                Console.WriteLine($"ManagementException in GetDeviceInfo: {ex.Message}");
                return null;
            }
        }

        private string GetPropertyValue(string input, string propertyName)
        {
            int startIndex = input.IndexOf(propertyName + "=\"", StringComparison.OrdinalIgnoreCase);
            if ( startIndex < 0 )
                return "";
            startIndex += propertyName.Length + 2;
            int endIndex = input.IndexOf("\"", startIndex);
            if (endIndex < 0)
                return "";
            return input[startIndex..endIndex];
        }

        private string GetValueFromDeviceId(string deviceId, string identifier)
        {
            if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(identifier))
                return "";
            int startIndex = deviceId.IndexOf(identifier, StringComparison.OrdinalIgnoreCase);
            if (startIndex < 0)
                return "";
            startIndex += identifier.Length;
            int endIndex = deviceId.IndexOfAny(['&' ,'\\'], startIndex);
            if (endIndex < 0)
                endIndex = deviceId.Length;
            return deviceId[startIndex..endIndex];
        }

        public void StopMonitoring()
        {
            insertWatcher.Stop();
            removeWatcher.Stop();
        }

        public List<USBDeviceInfo> GetConnectedDevices()
        {
            return new List<USBDeviceInfo>(_connectedDevices);
        }

        public List<USBDeviceInfo> GetConnectedDevicesFromSystem()
        {
                List<USBDeviceInfo> connectedDevices = [];
                ManagementObjectSearcher searcher = new(@"Select * From Win32_USBControllerDevice");
                foreach(ManagementBaseObject association in searcher.Get())
                {
                    string? dependentPath = association["Dependent"]?.ToString();
                    if (string.IsNullOrEmpty(dependentPath))
                    {
                        continue;
                    }

                    ManagementObject device = new(dependentPath);
                    USBDeviceInfo? deviceInfo = GetDeviceInfo(device);
                    if (deviceInfo != null)
                    {
                        connectedDevices.Add(deviceInfo);
                    }
                }
                return connectedDevices;
        }
    }
}
