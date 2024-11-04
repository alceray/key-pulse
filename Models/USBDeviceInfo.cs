using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.CompilerServices;
using System.Windows;

namespace KeyPulse.Models
{
    [Table("Devices")]
    public class USBDeviceInfo : INotifyPropertyChanged
    {
        public enum DeviceTypes
        {
            Unknown,
            Keyboard,
            Mouse,
            Other
        }

        [Key]
        [Required]
        public required string DeviceID { get; set; } = string.Empty;

        [Required]
        public DeviceTypes DeviceType { get; set; } =  DeviceTypes.Unknown;

        [Required]
        public required string VID { get; set; } = string.Empty;

        [Required]
        public required string PID { get; set; } = string.Empty;

        [Required]
        public string DeviceName 
        {
            get => _deviceName;
            set
            {
                if (_deviceName == value) return;
                _deviceName = value;
                Application.Current.Dispatcher.Invoke(() => OnPropertyChanged(nameof(DeviceName)));
            }
        }
        private string _deviceName = "Unknown Device";

        [Required]
        public bool IsConnected { 
            get => _isConnected;
            set
            {
                if (_isConnected != value)
                {
                    _isConnected = value;
                    Application.Current.Dispatcher.Invoke(() => OnPropertyChanged(nameof(IsConnected)));
                }
            } 
        }
        private bool _isConnected = false;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}