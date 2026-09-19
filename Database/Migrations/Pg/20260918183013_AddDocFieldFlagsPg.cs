using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations.Pg
{
    /// <inheritdoc />
    public partial class AddDocFieldFlagsPg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "isdocfile",
                table: "db_fields",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "isdoctype",
                table: "db_fields",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "isdocfile",
                table: "db_fields");

            migrationBuilder.DropColumn(
                name: "isdoctype",
                table: "db_fields");
        }
    }
}
