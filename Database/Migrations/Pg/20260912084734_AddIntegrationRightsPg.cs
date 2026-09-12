using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations.Pg
{
    /// <inheritdoc />
    public partial class AddIntegrationRightsPg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Пара к AddIntegrationRights (SQLite): те же два бита — IntegrationsSetup (8192)
            // и IntegrationsOutward (16384) — ставятся всем, у кого маска уже урезана.
            // Настройка обмена до этой правки права не спрашивала, и миграция не отбирает
            // ничего: она лишь дает администратору ручку, которой раньше не было.
            migrationBuilder.Sql("UPDATE db_permissions SET actionsmask = actionsmask | 24576");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE db_permissions SET actionsmask = actionsmask & ~24576");
        }
    }
}
