using KeyPulse.Helpers;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;

namespace KeyPulse.Models
{
    public enum DeviceTypes
    {
        Unknown,
        Keyboard,
        Mouse,
        Other
    }

    [Table("Devices")]
    public class DeviceInfo : ObservableObject
    {
        private bool _isActive = false;
        private string _deviceName = "Unknown Device";
        private readonly Stopwatch _currentSessionTimer = new();
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
                    if (_isActive)
                    {
                        _currentSessionTimer.Restart();
                    }
                    else
                    {
                        _currentSessionTimer.Reset();
                    }
                }
            }
        }

        [NotMapped]
        public TimeSpan CurrentSessionUsage => _currentSessionTimer.Elapsed;

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
}