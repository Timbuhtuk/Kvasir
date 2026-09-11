using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Entities.Models;


public partial class FinderSongDTO
{

    [Required]
    [MaxLength(128)]
    [Column("Name")]
    public string Name { get; set; } = null!;

    [Required]
    [MaxLength(128)]
    [Column("Author_name")]
    public string AuthorName { get; set; } = "NN";

    [Column("Duration", TypeName = "float")]
    public double? Duration { get; set; } = 0;

    [MaxLength(256)]
    [Column("Link")]
    public string? Link { get; set; }

}
