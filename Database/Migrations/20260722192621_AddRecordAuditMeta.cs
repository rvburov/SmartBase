using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddRecordAuditMeta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "db_records",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "UpdatedAt",
                table: "db_records",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "UpdatedBy",
                table: "db_records",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // Бэкфилл существующих записей: «Дата изменения» = «Дата создания» (истории правок
            // нет). CreatedBy/UpdatedBy остаются пустыми — автора старых записей мы не знаем.
            migrationBuilder.Sql("UPDATE db_records SET UpdatedAt = CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "db_records");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "db_records");

            migrationBuilder.DropColumn(
                name: "UpdatedBy",
                table: "db_records");
        }
    }
}
