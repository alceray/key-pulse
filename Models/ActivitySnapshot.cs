using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace KeyPulse.Models;

/// <summary>
/// Records input activity for a specific device during a one-minute window.
/// Keystrokes and mouse clicks are counted; mouse movement is binary (did it move at all).
/// </summary>
[Table("ActivitySnapshots")]
public class ActivitySnapshot
{
    /// <summary>Primary key.</summary>
    [Key]
    public int ActivitySnapshotId { get; set; }

    /// <summary>
    /// The USB device ID this snapshot relates to.
    /// Format: USB\VID_xxxx&PID_xxxx
    /// </summary>
    [Required]
    public string DeviceId { get; set; } = "";

    /// <summary>
    /// The start of the one-minute window this snapshot covers (seconds and below are zero).
    /// </summary>
    [Required]
    public DateTime Minute { get; set; }

    /// <summary>Number of key-down events recorded in this minute.</summary>
    public int Keystrokes { get; set; }

    /// <summary>Number of mouse button-down events recorded in this minute.</summary>
    public int MouseClicks { get; set; }

    /// <summary>
    /// Number of seconds within this minute (0–60) during which mouse movement was detected.
    /// Divide by 60.0 to get the fraction of the minute the mouse was active.
    /// </summary>
    public byte MouseActiveSeconds { get; set; }
}
