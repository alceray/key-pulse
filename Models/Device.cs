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
public class Device : ObservableObject
{
    private string _deviceName = "Unknown Device";
    private TimeSpan _storedTotalUsage = TimeSpan.Zero;
    private DateTime? _sessionStartedAt;
    private DateTime? _lastConnectedAt;
    private DateTime? _lastSeenAt;
    private bool _isActive;

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
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Whether the device is currently connected.
    /// </summary>
    [NotMapped]
    public bool IsConnected => _sessionStartedAt.HasValue;

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
            if (!_storedTotalUsage.Equals(value))
            {
                _storedTotalUsage = value;
                OnPropertyChanged();
            }
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
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsConnected));
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
                OnPropertyChanged();
                OnPropertyChanged(nameof(LastConnectedRelative));
            }
        }
    }

    /// <summary>
    /// Raw timestamp of the last device lifecycle event seen for this device.
    /// Persisted for fast dashboard reads.
    /// </summary>
    public DateTime? LastSeenAt
    {
        get => _lastSeenAt;
        set
        {
            if (_lastSeenAt != value)
            {
                _lastSeenAt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LastSeenRelative));
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
    /// Formatted relative time since the last device event.
    /// </summary>
    [NotMapped]
    public string LastSeenRelative => LastSeenAt.HasValue ? TimeFormatter.ToRelativeTime(LastSeenAt.Value) : "N/A";

    /// <summary>
    /// Whether a mouse click or keyboard stroke occurred recently (within activity hot window).
    /// </summary>
    [NotMapped]
    public bool IsActive
    {
        get => _isActive;
        private set
        {
            if (_isActive != value)
            {
                _isActive = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Sets live activity state (true while any key/button is currently held).
    /// </summary>
    public void SetActivityState(bool isActive)
    {
        IsActive = isActive;
    }

    /// <summary>
    /// Updates LastConnectedAt using event-aware rules:
    /// - always set on first known connection or Connected events
    /// - for ConnectionStarted, set only when the last completed session's
    ///   non-app events contain no ConnectionEnded for this device.
    /// </summary>
    public void UpdateLastConnectedAt(DateTime startTime, EventTypes eventType, IEnumerable<DeviceEvent> events)
    {
        if (!_lastConnectedAt.HasValue || eventType == EventTypes.Connected)
        {
            LastConnectedAt = startTime;
            return;
        }

        if (eventType != EventTypes.ConnectionStarted)
            return;

        var hasConnectionEndedInLastSession = events.Any(e =>
            e.DeviceId == DeviceId && e.EventType == EventTypes.ConnectionEnded
        );

        if (!hasConnectionEndedInLastSession)
            LastConnectedAt = startTime;
    }

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
        OnPropertyChanged(nameof(LastSeenRelative));
    }
}
