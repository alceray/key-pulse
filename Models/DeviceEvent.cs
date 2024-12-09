using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KeyPulse.Models
{
    public enum EventTypes
    {
        AppStarted,
        AppEnded,
        ConnectionStarted,
        ConnectionEnded,
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
        public required EventTypes EventType { get; set; }

        // device id should only be empty if event type is app started/ended
        [Required]
        public string DeviceId { get; set; } = "";
    }
}
