using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace KeyPulse.Models;

/// <summary>
/// Represents various device and application lifecycle events.
/// </summary>
public enum EventTypes
{
    /// <summary>Application started (devices may already be connected)</summary>
    AppStarted,

    /// <summary>Application ended (all devices marked disconnected)</summary>
    AppEnded,

    /// <summary>Device was already connected when app started (not user-plugged, only at startup)</summary>
    ConnectionStarted,

    /// <summary>Device was connected at startup, now being cleaned up on app shutdown</summary>
    ConnectionEnded,

    /// <summary>Device plugged in during runtime</summary>
    Connected,

    /// <summary>Device unplugged during runtime</summary>
    Disconnected,

    /// <summary>Device suspended (not currently used)</summary>
    Suspended,

    /// <summary>Device resumed (not currently used)</summary>
    Resumed,

    /// <summary>Error event (not currently used)</summary>
    Error,
}

/// <summary>
/// Extension methods for categorizing device event types based on their lifecycle state.
/// </summary>
public static class EventTypeExtensions
{
    /// <summary>Opening events indicate a device becoming active/connected.</summary>
    private static readonly List<EventTypes> _openingEvents =
    [
        EventTypes.ConnectionStarted,
        EventTypes.Connected,
        EventTypes.Resumed,
    ];

    /// <summary>Closing events indicate a device becoming inactive/disconnected.</summary>
    private static readonly List<EventTypes> _closingEvents =
    [
        EventTypes.ConnectionEnded,
        EventTypes.Disconnected,
        EventTypes.Suspended,
    ];

    /// <summary>Returns true if this event represents a device becoming active.</summary>
    public static bool IsOpening(this EventTypes eventType)
    {
        return _openingEvents.Contains(eventType);
    }

    /// <summary>Returns true if this event represents a device becoming inactive.</summary>
    public static bool IsClosing(this EventTypes eventType)
    {
        return _closingEvents.Contains(eventType);
    }
}

/// <summary>
/// Represents a single device or application lifecycle event.
/// Events are logged to track device connections, disconnections, and app lifecycle.
/// </summary>
[Table("DeviceEvents")]
public class DeviceEvent
{
    /// <summary>Primary key for the event record.</summary>
    [Key]
    public int DeviceEventId { get; set; }

    /// <summary>When the event occurred.</summary>
    [Required]
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>Type of event (connection, disconnection, app lifecycle, etc.).</summary>
    [Required]
    public required EventTypes EventType { get; set; }

    /// <summary>
    /// The USB device ID this event relates to.
    /// Only empty for AppStarted and AppEnded events.
    /// Format: USB\VID_xxxx&PID_xxxx
    /// </summary>
    [Required]
    public string DeviceId { get; set; } = "";
}
