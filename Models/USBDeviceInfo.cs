using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
            Others
        }
        [Key]
        public required string DeviceID { get; set; }
        public required string PnpDeviceID { get; set; }
        public DeviceTypes DeviceType { get; set; }
        public required string VID { get; set; }
        public required string PID { get; set; }
        public required string DeviceName { get; set; }
    }
}