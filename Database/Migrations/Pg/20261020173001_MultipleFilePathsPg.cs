using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations.Pg
{
    /// <summary>Безопасное раскрытие нового значения «Файла» на PostgreSQL 14 и новее.</summary>
    public partial class MultipleFilePathsPg : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION smartbase_file_paths(raw text)
                RETURNS TABLE(path text, file_index integer)
                LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE ROWS 1 AS $function$
                DECLARE parsed jsonb;
                BEGIN
                    IF raw IS NULL OR raw = '' THEN RETURN; END IF;
                    -- Одиночный путь не разбирается как JSON, в том числе путь с запятой.
                    IF left(raw, 1) = '{' THEN
                        BEGIN
                            parsed := raw::jsonb;
                            IF jsonb_typeof(parsed) = 'object' AND jsonb_typeof(parsed->'paths') = 'array' THEN
                                IF NOT EXISTS (SELECT 1 FROM jsonb_array_elements(parsed->'paths') AS part(value)
                                               WHERE jsonb_typeof(part.value) <> 'string') THEN
                                    RETURN QUERY
                                        SELECT part.value #>> '{}', (part.ordinality - 1)::integer
                                        FROM jsonb_array_elements(parsed->'paths') WITH ORDINALITY AS part(value, ordinality)
                                        WHERE part.value #>> '{}' <> '';
                                    RETURN;
                                END IF;
                            END IF;
                        EXCEPTION WHEN data_exception THEN
                            -- Неразбираемое значение прежнего типа остается одним путем.
                            NULL;
                        END;
                    END IF;
                    path := raw;
                    file_index := 0;
                    RETURN NEXT;
                END;
                $function$;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS smartbase_file_paths(text);");
        }
    }
}
