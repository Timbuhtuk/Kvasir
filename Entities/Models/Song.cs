using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Entities.Models;

/// <summary>
/// Represents song/track
/// </summary>
[Table("Song")]
public partial class Song : IEntity
{
    /// <summary>
    /// Primary key
    /// </summary>
    [Key]
    [Column("id")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>
    /// Song name
    /// </summary>
    [Required]
    [MaxLength(128)]
    [Column("Name")]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Author/performer name
    /// </summary>
    [Required]
    [MaxLength(128)]
    [Column("Author_name")]
    public string AuthorName { get; set; } = "NN";

    /// <summary>
    /// Duration in minutes (optional)
    /// </summary>
    [Column("Duration", TypeName = "float")]
    public double? Duration { get; set; }

    /// <summary>
    /// Source link (YouTube, etc.)
    /// </summary>
    [MaxLength(256)]
    [Column("Link")]
    public string? Link { get; set; }

    /// <summary>
    /// File path on disk
    /// </summary>
    [MaxLength(320)]
    [Column("File_path")]
    public string? FilePath { get; set; }

    /// <summary>
    /// View count
    /// </summary>
    [Required]
    [Column("Views")]
    public int Views { get; set; } = 0;

    public bool IsDownloaded { get; set; } = false;

    /// <summary>
    /// Navigation property: playlists containing this song
    /// </summary>
    [InverseProperty(nameof(Playlist.Songs))]
    public virtual ICollection<Playlist> Playlists { get; set; } = new List<Playlist>();
}
