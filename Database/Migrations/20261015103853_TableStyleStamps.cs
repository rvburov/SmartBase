using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations
{
    /// <inheritdoc />
    public partial class TableStyleStamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "BgAt",
                table: "record_display",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "FgAt",
                table: "record_display",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "HAlignAt",
                table: "record_display",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "VAlignAt",
                table: "record_display",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BgAt",
                table: "column_meta",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "FgAt",
                table: "column_meta",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "HAlignAt",
                table: "column_meta",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "VAlignAt",
                table: "column_meta",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BgAt",
                table: "record_display");

            migrationBuilder.DropColumn(
                name: "FgAt",
                table: "record_display");

            migrationBuilder.DropColumn(
                name: "HAlignAt",
                table: "record_display");

            migrationBuilder.DropColumn(
                name: "VAlignAt",
                table: "record_display");

            migrationBuilder.DropColumn(
                name: "BgAt",
                table: "column_meta");

            migrationBuilder.DropColumn(
                name: "FgAt",
                table: "column_meta");

            migrationBuilder.DropColumn(
                name: "HAlignAt",
                table: "column_meta");

            migrationBuilder.DropColumn(
                name: "VAlignAt",
                table: "column_meta");
        }
    }
}
