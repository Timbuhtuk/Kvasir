using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Entities.Models;

/// <summary>
/// Представляет гильдию Discord
/// </summary>
[Table("Guild")]
public partial class Guild : IEntity
{
    /// <summary>
    /// Первичный ключ
    /// </summary>
    [Key]
    [Column("id")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>
    /// Название гильдии
    /// </summary>
    [Required]
    [MaxLength(128)]
    [Column("Name")]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Discord ID гильдии
    /// </summary>
    [Required]
    [Column("Discord_id", TypeName = "decimal(20,0)")]
    public ulong DiscordId { get; set; }

    /// <summary>
    /// ID якорного сообщения (опционально)
    /// </summary>
    [Column("Anchor", TypeName = "decimal(20,0)")]
    public ulong? Anchor { get; set; }

    /// <summary>
    /// Навигационное свойство: плейлисты гильдии
    /// </summary>
    [InverseProperty(nameof(Playlist.Guilds))]
    public virtual ICollection<Playlist> Playlists { get; set; } = new List<Playlist>();
}
