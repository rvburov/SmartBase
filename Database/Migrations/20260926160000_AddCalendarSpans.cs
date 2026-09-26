using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations
{
    /// <summary>
    /// Сводка длительностей календаря «коллекция, весь день → наибольшая длительность события в
    /// секундах» (этап 8 плана drawn-grid.md). Событие, пересекающее момент T, началось не раньше
    /// T − M и кончается не позже своего начала + M, поэтому события, начавшиеся до окна, и самый
    /// поздний конец ищутся в полосе шириной M у границы окна. Без сводки такой поиск шел по всему
    /// будущему коллекции (агрегаты Диаграммы на 10 млн событий — 1 с), а сетки держались на
    /// допущении «событие со временем не длиннее 62 дней».
    ///
    /// Поддерживают ее триггеры на calendar_index — вставка и смена коллекции, вида или дат строки
    /// (upsert ON CONFLICT DO UPDATE в проекции поднимает триггер обновления). Граница только
    /// растет: удаление или укорочение события ее не уменьшает, она остается верхней оценкой —
    /// чтение от этого шире, но событий не теряет. Триггер срабатывает, только когда новая
    /// длительность больше записанной, — обычная запись в сводку не пишет вовсе. Нет строки —
    /// у коллекции нет событий этого вида. Неразобранная дата (julianday вернул NULL) дает
    /// «границы нет» (99 999 999 999 с), чтение тогда идет как без сводки.
    ///
    /// Первое заполнение — один проход по покрывающему индексу (CollectionId, AllDay, EndIso,
    /// StartIso, …): на 10 млн событий — 6–8 с под сплэшем, на обычной базе — мгновенно.
    /// </summary>
    public partial class AddCalendarSpans : Migration
    {
        // Длительность строки проекции в секундах, с запасом в секунду на дробную часть.
        private static string Span(string row) =>
            $@"max(0, COALESCE(CAST(ROUND((julianday({row}""EndIso"") - julianday({row}""StartIso"")) * 86400) AS INTEGER), 99999999998)) + 1";

        private const string Current = @"COALESCE((SELECT ""MaxSeconds"" FROM ""calendar_spans""
                     WHERE ""CollectionId"" = NEW.""CollectionId"" AND ""AllDay"" = NEW.""AllDay""), -1)";

        private static string Upsert => $@"
                    INSERT INTO ""calendar_spans"" (""CollectionId"", ""AllDay"", ""MaxSeconds"")
                    VALUES (NEW.""CollectionId"", NEW.""AllDay"", {Span("NEW.")})
                    ON CONFLICT (""CollectionId"", ""AllDay"") DO UPDATE SET ""MaxSeconds"" = max(""MaxSeconds"", excluded.""MaxSeconds"");";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE TABLE ""calendar_spans"" (
                    ""CollectionId"" INTEGER NOT NULL,
                    ""AllDay""       INTEGER NOT NULL,
                    ""MaxSeconds""   INTEGER NOT NULL,
                    CONSTRAINT ""PK_calendar_spans"" PRIMARY KEY (""CollectionId"", ""AllDay"")
                ) WITHOUT ROWID;");

            migrationBuilder.Sql($@"
                INSERT INTO ""calendar_spans"" (""CollectionId"", ""AllDay"", ""MaxSeconds"")
                SELECT ""CollectionId"", ""AllDay"", MAX({Span("")})
                FROM ""calendar_index""
                GROUP BY ""CollectionId"", ""AllDay"";");

            migrationBuilder.Sql($@"
                CREATE TRIGGER ""TR_calendar_index_spans_insert""
                AFTER INSERT ON ""calendar_index""
                WHEN {Span("NEW.")} > {Current}
                BEGIN{Upsert}
                END;");

            migrationBuilder.Sql($@"
                CREATE TRIGGER ""TR_calendar_index_spans_update""
                AFTER UPDATE OF ""CollectionId"", ""AllDay"", ""StartIso"", ""EndIso"" ON ""calendar_index""
                WHEN {Span("NEW.")} > {Current}
                BEGIN{Upsert}
                END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS ""TR_calendar_index_spans_update"";");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS ""TR_calendar_index_spans_insert"";");
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""calendar_spans"";");
        }
    }
}
