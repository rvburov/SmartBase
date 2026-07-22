using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations.Pg
{
    /// <inheritdoc />
    public partial class AddRecordAuditMetaPg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "createdby",
                table: "db_records",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "updatedat",
                table: "db_records",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "updatedby",
                table: "db_records",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Бэкфилл существующих записей: «Дата изменения» = «Дата создания» (истории правок
            // нет). createdby/updatedby остаются пустыми — автора старых записей мы не знаем.
            migrationBuilder.Sql("UPDATE db_records SET updatedat = createdat");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "createdby",
                table: "db_records");

            migrationBuilder.DropColumn(
                name: "updatedat",
                table: "db_records");

            migrationBuilder.DropColumn(
                name: "updatedby",
                table: "db_records");
        }
    }
}
