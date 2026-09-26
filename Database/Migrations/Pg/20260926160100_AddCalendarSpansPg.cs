using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations.Pg
{
    /// <summary>
    /// Сводка длительностей календаря «коллекция, весь день → наибольшая длительность события в
    /// секундах» для PostgreSQL — то же, что <c>AddCalendarSpans</c> для SQLite (почему и как —
    /// там). Длительность считает функция calendar_span_seconds: неразобранная дата не роняет
    /// запись проекции, а дает «границы нет». Имена в нижнем регистре, как во всей схеме
    /// PostgreSQL (ApplyLowercaseNames).
    /// </summary>
    public partial class AddCalendarSpansPg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE TABLE calendar_spans (
                    collectionid integer NOT NULL,
                    allday       integer NOT NULL,
                    maxseconds   bigint  NOT NULL,
                    CONSTRAINT pk_calendar_spans PRIMARY KEY (collectionid, allday)
                );");

            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION calendar_span_seconds(s text, e text) RETURNS bigint
                LANGUAGE plpgsql IMMUTABLE AS $$
                BEGIN
                    RETURN GREATEST(0, CEIL(EXTRACT(EPOCH FROM (e::timestamp - s::timestamp))))::bigint + 1;
                EXCEPTION WHEN others THEN
                    RETURN 99999999999;
                END
                $$;");

            migrationBuilder.Sql(@"
                INSERT INTO calendar_spans (collectionid, allday, maxseconds)
                SELECT collectionid, allday, MAX(calendar_span_seconds(startiso, endiso))
                FROM calendar_index
                GROUP BY collectionid, allday;");

            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION calendar_spans_sync() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    INSERT INTO calendar_spans (collectionid, allday, maxseconds)
                    VALUES (NEW.collectionid, NEW.allday, calendar_span_seconds(NEW.startiso, NEW.endiso))
                    ON CONFLICT (collectionid, allday)
                    DO UPDATE SET maxseconds = EXCLUDED.maxseconds
                    WHERE calendar_spans.maxseconds < EXCLUDED.maxseconds;
                    RETURN NULL;
                END
                $$;");

            migrationBuilder.Sql(@"
                CREATE TRIGGER tr_calendar_index_spans
                AFTER INSERT OR UPDATE OF collectionid, allday, startiso, endiso ON calendar_index
                FOR EACH ROW EXECUTE PROCEDURE calendar_spans_sync();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_calendar_index_spans ON calendar_index;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS calendar_spans_sync();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS calendar_span_seconds(text, text);");
            migrationBuilder.Sql("DROP TABLE IF EXISTS calendar_spans;");
        }
    }
}
