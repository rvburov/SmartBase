using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations.Pg
{
    /// <inheritdoc />
    public partial class AddDuplicateFieldRightPg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Пара к AddDuplicateFieldRight (SQLite): тот же перенос разрешения
            // «дублировать свойство» из CreateField (512) в новый бит DuplicateField (4096).
            migrationBuilder.Sql(
                "UPDATE db_permissions SET actionsmask = actionsmask | 4096 " +
                "WHERE (actionsmask & 512) = 512");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE db_permissions SET actionsmask = actionsmask & ~4096");
        }
    }
}
