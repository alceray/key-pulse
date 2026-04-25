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
}

/// <summary>
/// Extension methods for categorizing device event types based on their lifecycle state.
/// </summary>
public static class EventTypeExtensions
{
    /// <summary>Opening events indicate a device becoming active/connected.</summary>
    private static readonly List<EventTypes> OpeningEvents = [EventTypes.ConnectionStarted, EventTypes.Connected];

    /// <summary>Closing events indicate a device becoming inactive/disconnected.</summary>
    private static readonly List<EventTypes> ClosingEvents = [EventTypes.ConnectionEnded, EventTypes.Disconnected];

    /// <summary>App-level events that don't relate to specific devices.</summary>
    private static readonly List<EventTypes> AppEvents = [EventTypes.AppStarted, EventTypes.AppEnded];

    /// <summary>Returns true if this event represents a device becoming active.</summary>
    public static bool IsOpeningEvent(this EventTypes eventType)
    {
        return OpeningEvents.Contains(eventType);
    }

    /// <summary>Returns true if this event represents a device becoming inactive.</summary>
    public static bool IsClosingEvent(this EventTypes eventType)
    {
        return ClosingEvents.Contains(eventType);
    }

    /// <summary>Returns true if this event is an app-level event (AppStarted or AppEnded).</summary>
    public static bool IsAppEvent(this EventTypes eventType)
    {
        return AppEvents.Contains(eventType);
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
    public DateTime EventTime { get; set; } = DateTime.Now;

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
