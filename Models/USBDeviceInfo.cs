using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace KeyPulse.Models
{
    [Table("Devices")]
    public class USBDeviceInfo
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
        public required string PnpDeviceID { get; set; } = string.Empty;

        public DeviceTypes DeviceType { get; set; } =  DeviceTypes.Unknown;

        [Required]
        public required string VID { get; set; } = string.Empty;

        [Required]
        public required string PID { get; set; } = string.Empty;

        [Required]
        public required string DeviceName { get; set; } = "Unknown Device";
    }
}