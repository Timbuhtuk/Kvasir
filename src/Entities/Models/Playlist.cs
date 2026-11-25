using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Entities.Models;

/// <summary>
/// Represents music playlist
/// </summary>
[Table("Playlist")]
public partial class Playlist : IEntity
{
    /// <summary>
    /// Primary key
    /// </summary>
    [Key]
    [Column("id")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>
    /// Playlist name
    /// </summary>
    [Required]
    [MaxLength(128)]
    [Column("Name")]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Discord ID of playlist author
    /// </summary>
    [Required]
    [Column("Author_id", TypeName = "decimal(20,0)")]
    public ulong AuthorId { get; set; }

    /// <summary>
    /// Playlist creation date
    /// </summary>
    [Required]
    [Column("Creation_date", TypeName = "datetime2")]
    public DateTime CreationDate { get; set; }

    /// <summary>
    /// Playlist publicity flag
    /// </summary>
    [Required]
    [Column("Is_public")]
    public bool IsPublic { get; set; } = false;

    /// <summary>
    /// Navigation property: guilds using this playlist
    /// </summary>
    [InverseProperty(nameof(Guild.Playlists))]
    public virtual ICollection<Guild> Guilds { get; set; } = new List<Guild>();

    /// <summary>
    /// Navigation property: songs in playlist
    /// </summary>
    [InverseProperty(nameof(Song.Playlists))]
    public virtual ICollection<Song> Songs { get; set; } = new List<Song>();
}
