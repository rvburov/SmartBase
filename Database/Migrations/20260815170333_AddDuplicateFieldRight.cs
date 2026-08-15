using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddDuplicateFieldRight : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Схема не меняется: новое право — просто новый бит маски (DuplicateField = 4096).
            // Но раньше дублирование свойства спрашивало право создания (CreateField = 512),
            // и в сохранённых масках бита 4096 нет — без бэкфилла у всех, кому маску уже
            // урезали, дубль свойства запретился бы сам собой. Кто мог создавать свойства,
            // тот дублировал и раньше — переносим это разрешение.
            migrationBuilder.Sql(
                "UPDATE db_permissions SET ActionsMask = ActionsMask | 4096 " +
                "WHERE (ActionsMask & 512) = 512");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE db_permissions SET ActionsMask = ActionsMask & ~4096");
        }
    }
}
