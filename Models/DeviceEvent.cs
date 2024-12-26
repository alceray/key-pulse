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

    public static class EventTypeExtensions
    {
        private static readonly List<EventTypes> _openingEvents = [EventTypes.ConnectionStarted, EventTypes.Connected, EventTypes.Resumed];
        private static readonly List<EventTypes> _closingEvents = [EventTypes.ConnectionEnded, EventTypes.Disconnected, EventTypes.Suspended];
        public static bool IsOpening(this EventTypes eventType)
        {
            return _openingEvents.Contains(eventType);
        }
        public static bool IsClosing(this EventTypes eventType)
        {
            return _closingEvents.Contains(eventType);
        }
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
