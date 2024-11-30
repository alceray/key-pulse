using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KeyPulse.Models
{
    public enum DeviceEventType
    {
        ConnectionClosed,
        ConnectionOpened,
        Connected,
        Disconnected,
        Suspended,
        Resumed,
        Error
    }

    [Table("DeviceEvents")]
    public class DeviceEvent
    {
        [Key]
        public int DeviceEventId { get; set; }

        [Required]
        public DateTime Timestamp { get; set; } = DateTime.Now;

        [Required]
        public required DeviceEventType EventType { get; set; }

        [Required]
        public required string DeviceId { get; set; }

        [ForeignKey("DeviceId")]
        public virtual DeviceInfo Device { get; set; } = null!;
    }
}
