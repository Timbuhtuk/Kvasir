using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Entities.Models;

/// <summary>
/// Представляет плейлист музыки
/// </summary>
[Table("Playlist")]
public partial class Playlist : IEntity
{
    /// <summary>
    /// Первичный ключ
    /// </summary>
    [Key]
    [Column("id")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>
    /// Название плейлиста
    /// </summary>
    [Required]
    [MaxLength(128)]
    [Column("Name")]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Discord ID автора плейлиста
    /// </summary>
    [Required]
    [Column("Author_id", TypeName = "decimal(20,0)")]
    public ulong AuthorId { get; set; }

    /// <summary>
    /// Дата создания плейлиста
    /// </summary>
    [Required]
    [Column("Creation_date", TypeName = "datetime2")]
    public DateTime CreationDate { get; set; }

    /// <summary>
    /// Флаг публичности плейлиста
    /// </summary>
    [Required]
    [Column("Is_public")]
    public bool IsPublic { get; set; } = false;

    /// <summary>
    /// Навигационное свойство: гильдии, использующие этот плейлист
    /// </summary>
    [InverseProperty(nameof(Guild.Playlists))]
    public virtual ICollection<Guild> Guilds { get; set; } = new List<Guild>();

    /// <summary>
    /// Навигационное свойство: песни в плейлисте
    /// </summary>
    [InverseProperty(nameof(Song.Playlists))]
    public virtual ICollection<Song> Songs { get; set; } = new List<Song>();
}
