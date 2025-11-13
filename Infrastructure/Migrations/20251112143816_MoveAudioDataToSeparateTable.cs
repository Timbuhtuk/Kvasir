using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MoveAudioDataToSeparateTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Создаем таблицу SongAudioData
            migrationBuilder.CreateTable(
                name: "SongAudioData",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Song_id = table.Column<int>(type: "int", nullable: false),
                    Audio_data = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SongAudioData", x => x.id);
                    table.ForeignKey(
                        name: "FK_SongAudioData_Song_Song_id",
                        column: x => x.Song_id,
                        principalTable: "Song",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SongAudioData_Song_id",
                table: "SongAudioData",
                column: "Song_id",
                unique: true);

            // Переносим данные из Song.Audio_data в SongAudioData
            migrationBuilder.Sql(@"
                INSERT INTO SongAudioData (Song_id, Audio_data)
                SELECT id, Audio_data
                FROM Song
                WHERE Audio_data IS NOT NULL
            ");

            // Удаляем колонку Audio_data из Song
            migrationBuilder.DropColumn(
                name: "Audio_data",
                table: "Song");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Добавляем колонку Audio_data обратно в Song
            migrationBuilder.AddColumn<byte[]>(
                name: "Audio_data",
                table: "Song",
                type: "varbinary(max)",
                nullable: true);

            // Переносим данные обратно из SongAudioData в Song.Audio_data
            migrationBuilder.Sql(@"
                UPDATE s
                SET s.Audio_data = sad.Audio_data
                FROM Song s
                INNER JOIN SongAudioData sad ON s.id = sad.Song_id
            ");

            // Удаляем таблицу SongAudioData
            migrationBuilder.DropTable(
                name: "SongAudioData");
        }
    }
}
