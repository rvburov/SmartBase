using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartBase.Database.Migrations.Pg
{
    /// <summary>
    /// PostgreSQL-близнец миграции TaskEvents: очередь уведомлений по задачам (task_events).
    /// Имена колонок в нижнем регистре — так их видит общий сырой SQL репозиториев
    /// (см. AppDbContext.ApplyLowercaseNames). Обоснование схемы — в SQLite-миграции.
    /// </summary>
    /// <inheritdoc />
    public partial class TaskEventsPg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "task_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ts = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    recipientkey = table.Column<string>(type: "text", nullable: false),
                    recipientname = table.Column<string>(type: "text", nullable: false),
                    actorkey = table.Column<string>(type: "text", nullable: false),
                    actorname = table.Column<string>(type: "text", nullable: false),
                    recordid = table.Column<int>(type: "integer", nullable: false),
                    collectionid = table.Column<int>(type: "integer", nullable: false),
                    fieldid = table.Column<int>(type: "integer", nullable: false),
                    execfieldid = table.Column<int>(type: "integer", nullable: false),
                    tasktitle = table.Column<string>(type: "text", nullable: false),
                    collectionname = table.Column<string>(type: "text", nullable: false),
                    oldvalue = table.Column<string>(type: "text", nullable: false),
                    newvalue = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_task_events_recipientkey_id",
                table: "task_events",
                columns: new[] { "recipientkey", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "task_events");
        }
    }
}
