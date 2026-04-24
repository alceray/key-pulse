using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using KeyPulse.Helpers;

namespace KeyPulse.Models;

/// <summary>
/// Categorizes the type of USB device (keyboard, mouse, etc.).
/// </summary>
public enum DeviceTypes
{
    /// <summary>Device type has not been determined yet.</summary>
    Unknown,

    /// <summary>USB keyboard device.</summary>
    Keyboard,

    /// <summary>USB mouse or pointing device.</summary>
    Mouse,

    /// <summary>Other USB input device.</summary>
    Other,
}

/// <summary>
/// Represents a connected USB input device (keyboard, mouse, etc.).
/// Tracks device metadata, connection status, and usage statistics.
/// </summary>
[Table("Devices")]
public class DeviceInfo : ObservableObject
{
    private string _deviceName = "Unknown Device";
    private TimeSpan _storedTotalUsage = TimeSpan.Zero;
    private DateTime? _sessionStartedAt;
    private DateTime? _lastConnectedAt;

    /// <summary>
    /// Unique identifier for the device in format: USB\VID_xxxx&PID_xxxx
    /// </summary>
    [Key]
    public required string DeviceId { get; set; }

    /// <summary>
    /// Categorizes the device type (keyboard, mouse, other, etc.).
    /// </summary>
    [Required]
    public DeviceTypes DeviceType { get; set; } = DeviceTypes.Unknown;

    /// <summary>
    /// User-friendly name for the device (e.g., "Logitech MX Master 3").
    /// Notifies UI when changed so it can be persisted.
    /// </summary>
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

    /// <summary>
    /// Whether the device is currently connected and active.
    /// Computed from whether SessionStartedAt has a value.
    /// </summary>
    [NotMapped]
    public bool IsActive => _sessionStartedAt.HasValue;

    /// <summary>
    /// Cumulative usage time snapshot rebuilt from connection event boundaries.
    /// While active, display value ticks by adding elapsed time since SessionStartedAt.
    /// </summary>
    public TimeSpan TotalUsage
    {
        get
        {
            if (!_sessionStartedAt.HasValue)
                return _storedTotalUsage;

            var elapsed = DateTime.Now - _sessionStartedAt.Value;
            return _storedTotalUsage + (elapsed > TimeSpan.Zero ? elapsed : TimeSpan.Zero);
        }
        set
        {
            _storedTotalUsage = value;
            OnPropertyChanged(nameof(TotalUsage));
        }
    }

    /// <summary>
    /// Raw timestamp of the currently active session.
    /// Set when a ConnectionStarted event is added and cleared on ConnectionEnded.
    /// </summary>
    public DateTime? SessionStartedAt
    {
        get => _sessionStartedAt;
        set
        {
            if (_sessionStartedAt != value)
            {
                _sessionStartedAt = value;
                OnPropertyChanged(nameof(SessionStartedAt));
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(TotalUsage));
            }
        }
    }

    /// <summary>
    /// Raw timestamp of the last connection — persisted for fast loading/sorting.
    /// </summary>
    public DateTime? LastConnectedAt
    {
        get => _lastConnectedAt;
        set
        {
            if (_lastConnectedAt != value)
            {
                _lastConnectedAt = value;
                OnPropertyChanged(nameof(LastConnectedAt));
                OnPropertyChanged(nameof(LastConnectedRelative));
            }
        }
    }

    /// <summary>
    /// Formatted relative time since last connection (e.g., "2 hours ago").
    /// Computed from the persisted raw timestamp.
    /// </summary>
    [NotMapped]
    public string LastConnectedRelative =>
        LastConnectedAt.HasValue ? TimeFormatter.ToRelativeTime(LastConnectedAt.Value) : "N/A";

    /// <summary>
    /// Commits elapsed time from the active session into stored usage,
    /// then marks the device as inactive.
    /// </summary>
    public void CommitSessionUsage(DateTime endTime)
    {
        if (!_sessionStartedAt.HasValue)
            return;

        var elapsed = endTime - _sessionStartedAt.Value;
        if (elapsed > TimeSpan.Zero)
            _storedTotalUsage += elapsed;

        SessionStartedAt = null;
    }

    /// <summary>
    /// Refreshes dynamic display-only properties that depend on the current time.
    /// </summary>
    public void RefreshDynamicProperties()
    {
        OnPropertyChanged(nameof(TotalUsage));
        OnPropertyChanged(nameof(LastConnectedRelative));
    }
}
