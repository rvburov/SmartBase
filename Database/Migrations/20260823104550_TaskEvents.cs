using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations
{
    /// <summary>
    /// Очередь уведомлений по задачам: «вам назначена задача» исполнителю и «задача сменила
    /// стадию» тому, кто указан в свойстве «Назначил». Строку пишет тот, кто правит значение
    /// (ConstructorRepository.TryWriteTaskEvent), читает — клиент получателя по курсору.
    ///
    /// Дешевая миграция: создание пустой таблицы и одного индекса, прохода по данным нет —
    /// мгновенна на базе любого размера.
    ///
    /// Без внешних ключей — намеренно, как у change_log: уведомление обязано доехать, даже
    /// если запись или пользователя успели удалить, а каскад молча уносил бы очередь.
    /// Индекс (RecipientKey, Id) нужен догону после долгого простоя: обычный тик берет
    /// диапазон по PK, а вот «мои события среди тысяч чужих» — уже индексом.
    /// </summary>
    /// <inheritdoc />
    public partial class TaskEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "task_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Ts = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    RecipientKey = table.Column<string>(type: "TEXT", nullable: false),
                    RecipientName = table.Column<string>(type: "TEXT", nullable: false),
                    ActorKey = table.Column<string>(type: "TEXT", nullable: false),
                    ActorName = table.Column<string>(type: "TEXT", nullable: false),
                    RecordId = table.Column<int>(type: "INTEGER", nullable: false),
                    CollectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    FieldId = table.Column<int>(type: "INTEGER", nullable: false),
                    ExecFieldId = table.Column<int>(type: "INTEGER", nullable: false),
                    TaskTitle = table.Column<string>(type: "TEXT", nullable: false),
                    CollectionName = table.Column<string>(type: "TEXT", nullable: false),
                    OldValue = table.Column<string>(type: "TEXT", nullable: false),
                    NewValue = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_task_events_RecipientKey_Id",
                table: "task_events",
                columns: new[] { "RecipientKey", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "task_events");
        }
    }
}
