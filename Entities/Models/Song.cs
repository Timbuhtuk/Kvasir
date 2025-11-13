using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Entities.Models;

/// <summary>
/// Представляет песню/трек
/// </summary>
[Table("Song")]
public partial class Song : IEntity
{
    /// <summary>
    /// Первичный ключ
    /// </summary>
    [Key]
    [Column("id")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>
    /// Название песни
    /// </summary>
    [Required]
    [MaxLength(128)]
    [Column("Name")]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Имя автора/исполнителя
    /// </summary>
    [Required]
    [MaxLength(128)]
    [Column("Author_name")]
    public string AuthorName { get; set; } = "NN";

    /// <summary>
    /// Длительность в минутах (опционально)
    /// </summary>
    [Column("Duration", TypeName = "float")]
    public double? Duration { get; set; }

    /// <summary>
    /// Ссылка на источник (YouTube, и т.д.)
    /// </summary>
    [MaxLength(256)]
    [Column("Link")]
    public string? Link { get; set; }

    /// <summary>
    /// Путь к файлу на диске
    /// </summary>
    [MaxLength(320)]
    [Column("File_path")]
    public string? FilePath { get; set; }

    /// <summary>
    /// Количество просмотров
    /// </summary>
    [Required]
    [Column("Views")]
    public int Views { get; set; } = 0;

    /// <summary>
    /// Навигационное свойство: плейлисты, содержащие эту песню
    /// </summary>
    [InverseProperty(nameof(Playlist.Songs))]
    public virtual ICollection<Playlist> Playlists { get; set; } = new List<Playlist>();
}
