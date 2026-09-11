using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Entities.Models;

/// <summary>
/// Represents Discord guild
/// </summary>
[Table("Guild")]
public partial class Guild : IEntity
{
    /// <summary>
    /// Primary key
    /// </summary>
    [Key]
    [Column("id")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>
    /// Guild name
    /// </summary>
    [Required]
    [MaxLength(128)]
    [Column("Name")]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Discord guild ID
    /// </summary>
    [Required]
    [Column("Discord_id", TypeName = "decimal(20,0)")]
    public ulong DiscordId { get; set; }

    /// <summary>
    /// Anchor message ID (optional)
    /// </summary>
    [Column("Anchor", TypeName = "decimal(20,0)")]
    public ulong? Anchor { get; set; }

    /// <summary>
    /// Navigation property: guild playlists
    /// </summary>
    [InverseProperty(nameof(Playlist.Guilds))]
    public virtual ICollection<Playlist> Playlists { get; set; } = new List<Playlist>();
}
