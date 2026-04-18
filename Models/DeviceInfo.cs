using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using KeyPulse.Helpers;

namespace KeyPulse.Models;

public enum DeviceTypes
{
    Unknown,
    Keyboard,
    Mouse,
    Other,
}

[Table("Devices")]
public class DeviceInfo : ObservableObject
{
    private bool _isActive = false;
    private string _deviceName = "Unknown Device";
    private DateTime? _currentSessionStartUtc;
    private TimeSpan _totalUsage = TimeSpan.Zero;

    [Key]
    public required string DeviceId { get; set; }

    [Required]
    public DeviceTypes DeviceType { get; set; } = DeviceTypes.Unknown;

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

    [NotMapped]
    public TimeSpan CurrentSessionUsage =>
        _currentSessionStartUtc.HasValue
            ? DateTime.UtcNow - _currentSessionStartUtc.Value
            : TimeSpan.Zero;

    public void BeginSession(DateTime? sessionStartUtc = null)
    {
        _currentSessionStartUtc = sessionStartUtc ?? DateTime.UtcNow;
        OnPropertyChanged(nameof(CurrentSessionUsage));
    }

    public void EndSession()
    {
        _currentSessionStartUtc = null;
        OnPropertyChanged(nameof(CurrentSessionUsage));
    }

    // The new closing device event must be saved before calculating total usage to get the most accurate result
    public TimeSpan TotalUsage
    {
        get => _totalUsage;
        set
        {
            _totalUsage = value;
            OnPropertyChanged(nameof(TotalUsage));
        }
    }

    public void UpdateCurrentSessionUsage()
    {
        OnPropertyChanged(nameof(CurrentSessionUsage));
    }
}
