using Entities.Models;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure;

public partial class DiscordMusicDBContext : DbContext
{
    public DiscordMusicDBContext() {
    }

    public DiscordMusicDBContext(DbContextOptions<DiscordMusicDBContext> options)
        : base(options) {
    }

    public virtual DbSet<Guild> Guilds { get; set; }
    public virtual DbSet<Playlist> Playlists { get; set; }
    public virtual DbSet<Song> Songs { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.Entity<Guild>()
            .HasMany(g => g.Playlists)
            .WithMany(p => p.Guilds)
            .UsingEntity(j => j.ToTable("GuildPlaylist"));

        modelBuilder.Entity<Song>()
            .HasMany(s => s.Playlists)
            .WithMany(p => p.Songs)
            .UsingEntity(j => j.ToTable("SongPlaylist"));

        modelBuilder.Entity<Song>(entity => {
            entity.Property(e => e.AuthorName)
                .HasDefaultValue("NN");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
