using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations
{
    /// <summary>
    /// Сводка календаря «коллекция, день начала, поле-дата → число событий» (этап 8 плана
    /// drawn-grid.md). Счетчики дней — «Журнал» за всю историю, числа «Года», «+N еще» «Месяца»,
    /// «показано N из …» «Расписания» — раньше считались по самим строкам проекции: 10 млн
    /// событий — это 10 млн шагов индекса, секунды на каждый заход. Сводка держит готовые числа:
    /// полный день окна — одна строка на поле, счет не зависит от объема.
    ///
    /// Поддерживают ее триггеры на calendar_index — ровно там, где меняется проекция, поэтому ни
    /// один путь записи (правка ячейки, пакетные дубль и удаление, перенос между базами, копия
    /// старой версии программы на общей базе) не может ее обойти. Проекция пишется INSERT,
    /// DELETE и upsert ON CONFLICT DO UPDATE (он поднимает триггер обновления); INSERT OR REPLACE
    /// в calendar_index не используется — его скрытое удаление триггеров не поднимает.
    ///
    /// Удаление только уменьшает счетчик: нулевые строки чтение пропускает, а подключение базы
    /// их убирает (ConstructorRepository.TidyCalendarDayCounts) — массовое удаление не платит за
    /// каждую строку вторым DELETE. Поле-дата в ключе, потому что видимость поля — фильтр
    /// чтения: скрытое поле по-прежнему в проекции и в сводке, чтение его отсекает.
    ///
    /// Первое заполнение — один GROUP BY по проекции: на 10 млн событий — секунды под сплэшем.
    /// </summary>
    public partial class AddCalendarDayCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE TABLE ""calendar_day_counts"" (
                    ""CollectionId"" INTEGER NOT NULL,
                    ""StartDay""     TEXT    NOT NULL,
                    ""FieldId""      INTEGER NOT NULL,
                    ""Events""       INTEGER NOT NULL,
                    CONSTRAINT ""PK_calendar_day_counts"" PRIMARY KEY (""CollectionId"", ""StartDay"", ""FieldId"")
                ) WITHOUT ROWID;");

            migrationBuilder.Sql(@"
                INSERT INTO ""calendar_day_counts"" (""CollectionId"", ""StartDay"", ""FieldId"", ""Events"")
                SELECT ""CollectionId"", substr(""StartIso"", 1, 10), ""FieldId"", COUNT(*)
                FROM ""calendar_index""
                GROUP BY ""CollectionId"", substr(""StartIso"", 1, 10), ""FieldId"";");

            migrationBuilder.Sql(@"
                CREATE TRIGGER ""TR_calendar_index_day_counts_insert""
                AFTER INSERT ON ""calendar_index""
                BEGIN
                    INSERT INTO ""calendar_day_counts"" (""CollectionId"", ""StartDay"", ""FieldId"", ""Events"")
                    VALUES (NEW.""CollectionId"", substr(NEW.""StartIso"", 1, 10), NEW.""FieldId"", 1)
                    ON CONFLICT (""CollectionId"", ""StartDay"", ""FieldId"") DO UPDATE SET ""Events"" = ""Events"" + 1;
                END;");

            migrationBuilder.Sql(@"
                CREATE TRIGGER ""TR_calendar_index_day_counts_delete""
                AFTER DELETE ON ""calendar_index""
                BEGIN
                    UPDATE ""calendar_day_counts"" SET ""Events"" = ""Events"" - 1
                    WHERE ""CollectionId"" = OLD.""CollectionId""
                      AND ""StartDay""     = substr(OLD.""StartIso"", 1, 10)
                      AND ""FieldId""      = OLD.""FieldId"";
                END;");

            migrationBuilder.Sql(@"
                CREATE TRIGGER ""TR_calendar_index_day_counts_update""
                AFTER UPDATE OF ""CollectionId"", ""StartIso"", ""FieldId"" ON ""calendar_index""
                WHEN OLD.""CollectionId"" IS NOT NEW.""CollectionId""
                  OR OLD.""FieldId"" IS NOT NEW.""FieldId""
                  OR substr(OLD.""StartIso"", 1, 10) IS NOT substr(NEW.""StartIso"", 1, 10)
                BEGIN
                    UPDATE ""calendar_day_counts"" SET ""Events"" = ""Events"" - 1
                    WHERE ""CollectionId"" = OLD.""CollectionId""
                      AND ""StartDay""     = substr(OLD.""StartIso"", 1, 10)
                      AND ""FieldId""      = OLD.""FieldId"";
                    INSERT INTO ""calendar_day_counts"" (""CollectionId"", ""StartDay"", ""FieldId"", ""Events"")
                    VALUES (NEW.""CollectionId"", substr(NEW.""StartIso"", 1, 10), NEW.""FieldId"", 1)
                    ON CONFLICT (""CollectionId"", ""StartDay"", ""FieldId"") DO UPDATE SET ""Events"" = ""Events"" + 1;
                END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS ""TR_calendar_index_day_counts_update"";");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS ""TR_calendar_index_day_counts_delete"";");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS ""TR_calendar_index_day_counts_insert"";");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""calendar_day_counts"";");
        }
    }
}
