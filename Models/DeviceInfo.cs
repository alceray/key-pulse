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
    private TimeSpan _totalUsage = TimeSpan.Zero;

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
    /// Cumulative usage time across all sessions.
    /// Must be updated after device disconnection events to capture the final session duration.
    /// Persisted to database.
    /// </summary>
    public TimeSpan TotalUsage
    {
        get => _totalUsage;
        set
        {
            _totalUsage = value;
            OnPropertyChanged(nameof(TotalUsage));
        }
    }

    /// <summary>
    /// Caches the last connected time relative string.
    /// </summary>
    private string _lastConnectedRelative = "N/A";

    /// <summary>
    /// Raw UTC timestamp of the last connection — used as sort key for the UI column.
    /// Not persisted to database; populated from event history.
    /// </summary>
    [NotMapped]
    public DateTime? LastConnectedAt { get; set; }

    /// <summary>
    /// Formatted relative time since last connection (e.g., "2 hours ago").
    /// Computed from event history.
    /// </summary>
    [NotMapped]
    public string LastConnectedRelative
    {
        get => _lastConnectedRelative;
        set
        {
            if (_lastConnectedRelative != value)
            {
                _lastConnectedRelative = value;
                OnPropertyChanged(nameof(LastConnectedRelative));
            }
        }
    }
}
