using KeyPulse.Helpers;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.CompilerServices;
using System.Windows;

namespace KeyPulse.Models
{
    public enum DeviceTypes
    {
        Unknown,
        Keyboard,
        Mouse,
        Other
    }

    [Table("Devices")]
    public class DeviceInfo : ObservableObject
    {
        [Key]
        public required string DeviceId { get; set; }

        [Required]
        public DeviceTypes DeviceType { get; set; } = DeviceTypes.Unknown;

        [Required]
        public required string VID { get; set; }

        [Required]
        public required string PID { get; set; }

        [Required]
        public string DeviceName 
        {
            get => _deviceName;
            set
            {
                if (_deviceName != value)
                {
                    _deviceName = value;
                    OnPropertyChanged(nameof(DeviceName));
                }
            }
        }
        private string _deviceName = "Unknown Device";

        [Required]
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive != value)
                {
                    _isActive = value;
                    OnPropertyChanged(nameof(IsActive));
                }
            }
        }
        private bool _isActive = true;

        public virtual ObservableCollection<DeviceEvent> DeviceEventList { get; } = [];
    }
}