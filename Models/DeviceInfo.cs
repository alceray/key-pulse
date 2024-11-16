using KeyPulse.Helpers;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.CompilerServices;
using System.Windows;

namespace KeyPulse.Models
{
    [Table("Devices")]
    public class DeviceInfo : ObservableObject
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
        public required string DeviceID { get; set; }

        [Required]
        public DeviceTypes DeviceType { get; set; } =  DeviceTypes.Unknown;

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

        public virtual ICollection<Connection> Connections { get; } = [];

        [NotMapped]
        public bool IsConnected => Connections.Any(c => c.DisconnectedAt == null);
    }
}