using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddDocFieldFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDocFile",
                table: "db_fields",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDocType",
                table: "db_fields",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsDocFile",
                table: "db_fields");

            migrationBuilder.DropColumn(
                name: "IsDocType",
                table: "db_fields");
        }
    }
}
