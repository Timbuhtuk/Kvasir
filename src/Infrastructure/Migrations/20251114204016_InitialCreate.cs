using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Guild",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Discord_id = table.Column<ulong>(type: "decimal(20,0)", nullable: false),
                    Anchor = table.Column<ulong>(type: "decimal(20,0)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Guild", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "Playlist",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Author_id = table.Column<ulong>(type: "decimal(20,0)", nullable: false),
                    Creation_date = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Is_public = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Playlist", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "Song",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Author_name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, defaultValue: "NN"),
                    Duration = table.Column<double>(type: "float", nullable: true),
                    Link = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    File_path = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    Views = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Song", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "GuildPlaylist",
                columns: table => new
                {
                    GuildsId = table.Column<int>(type: "INTEGER", nullable: false),
                    PlaylistsId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildPlaylist", x => new { x.GuildsId, x.PlaylistsId });
                    table.ForeignKey(
                        name: "FK_GuildPlaylist_Guild_GuildsId",
                        column: x => x.GuildsId,
                        principalTable: "Guild",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GuildPlaylist_Playlist_PlaylistsId",
                        column: x => x.PlaylistsId,
                        principalTable: "Playlist",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SongPlaylist",
                columns: table => new
                {
                    PlaylistsId = table.Column<int>(type: "INTEGER", nullable: false),
                    SongsId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SongPlaylist", x => new { x.PlaylistsId, x.SongsId });
                    table.ForeignKey(
                        name: "FK_SongPlaylist_Playlist_PlaylistsId",
                        column: x => x.PlaylistsId,
                        principalTable: "Playlist",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SongPlaylist_Song_SongsId",
                        column: x => x.SongsId,
                        principalTable: "Song",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GuildPlaylist_PlaylistsId",
                table: "GuildPlaylist",
                column: "PlaylistsId");

            migrationBuilder.CreateIndex(
                name: "IX_SongPlaylist_SongsId",
                table: "SongPlaylist",
                column: "SongsId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GuildPlaylist");

            migrationBuilder.DropTable(
                name: "SongPlaylist");

            migrationBuilder.DropTable(
                name: "Guild");

            migrationBuilder.DropTable(
                name: "Playlist");

            migrationBuilder.DropTable(
                name: "Song");
        }
    }
}
