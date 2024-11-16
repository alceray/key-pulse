using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KeyPulse.Models
{
    [Table("Connections")]
    public class Connection
    {
        [Key]
        public int ConnectionID { get; set; }

        [Required]
        public DateTime ConnectedAt { get; set; } = DateTime.Now;

        public DateTime? DisconnectedAt { get; set; }

        [NotMapped]
        public TimeSpan Duration => DisconnectedAt.HasValue ? DisconnectedAt.Value  - ConnectedAt : DateTime.Now - ConnectedAt;

        [Required]
        public required string DeviceID { get; set; }

        [ForeignKey("DeviceID")]
        public virtual DeviceInfo Device { get; set; } = null!;
    }
}
