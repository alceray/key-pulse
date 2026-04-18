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
    private DateTime? _currentSessionStartUtc;
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
    /// Calculates usage time for the current session (from BeginSession to now).
    /// Returns zero if device is not actively connected.
    /// Not persisted to database; calculated on-the-fly.
    /// </summary>
    [NotMapped]
    public TimeSpan CurrentSessionUsage =>
        _currentSessionStartUtc.HasValue
            ? DateTime.UtcNow - _currentSessionStartUtc.Value
            : TimeSpan.Zero;

    /// <summary>
    /// Marks the start of a device usage session.
    /// </summary>
    /// <param name="sessionStartUtc">Optional UTC timestamp; defaults to now if not provided.</param>
    public void BeginSession(DateTime? sessionStartUtc = null)
    {
        _currentSessionStartUtc = sessionStartUtc ?? DateTime.UtcNow;
        OnPropertyChanged(nameof(CurrentSessionUsage));
    }

    /// <summary>
    /// Marks the end of the current device usage session.
    /// </summary>
    public void EndSession()
    {
        _currentSessionStartUtc = null;
        OnPropertyChanged(nameof(CurrentSessionUsage));
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
    /// Forces a refresh of the CurrentSessionUsage UI binding.
    /// Called when the timer updates the displayed usage in real-time.
    /// </summary>
    public void UpdateCurrentSessionUsage()
    {
        OnPropertyChanged(nameof(CurrentSessionUsage));
    }
}
