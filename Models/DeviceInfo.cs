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
    private bool _isActive = false;
    private string _deviceName = "Unknown Device";
    private TimeSpan _storedTotalUsage = TimeSpan.Zero;
    private DateTime? _lastConnectedAt;
    private DateTime? _activeSessionStartedAtUtc;

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
    /// Notifies UI when changed.
    /// </summary>
    [Required]
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive != value)
            {
                _isActive = value;
                OnPropertyChanged(nameof(IsActive));
            }
        }
    }

    /// <summary>
    /// Cumulative usage time across all completed sessions.
    /// When the device is active, the current session duration is added in-memory for display.
    /// The stored value is only persisted on event boundaries.
    /// </summary>
    public TimeSpan TotalUsage
    {
        get =>
            _storedTotalUsage
            + (
                IsActive && _activeSessionStartedAtUtc.HasValue
                    ? DateTime.UtcNow - _activeSessionStartedAtUtc.Value
                    : TimeSpan.Zero
            );
        set
        {
            _storedTotalUsage = value;
            OnPropertyChanged(nameof(TotalUsage));
        }
    }

    /// <summary>
    /// Starts tracking the current active session in memory.
    /// </summary>
    public void StartActiveSession(DateTime? sessionStartedAtUtc = null)
    {
        _activeSessionStartedAtUtc = sessionStartedAtUtc ?? DateTime.UtcNow;
        OnPropertyChanged(nameof(TotalUsage));
    }

    /// <summary>
    /// Finishes the current active session and folds it into the persisted usage snapshot.
    /// </summary>
    public void EndActiveSession(DateTime? sessionEndedAtUtc = null)
    {
        if (_activeSessionStartedAtUtc.HasValue)
        {
            var end = sessionEndedAtUtc ?? DateTime.UtcNow;
            if (end > _activeSessionStartedAtUtc.Value)
                _storedTotalUsage += end - _activeSessionStartedAtUtc.Value;
            _activeSessionStartedAtUtc = null;
        }

        OnPropertyChanged(nameof(TotalUsage));
    }

    /// <summary>
    /// Raw UTC timestamp of the last connection — persisted for fast loading/sorting.
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
    /// Refreshes dynamic display-only properties that depend on the current time.
    /// </summary>
    public void RefreshDynamicProperties()
    {
        OnPropertyChanged(nameof(TotalUsage));
        OnPropertyChanged(nameof(LastConnectedRelative));
    }
}
