using SmartBase.Core.Importers;
using SmartBase.Core.Importers.TableSources;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Потоковый файловый источник JSON (<see cref="JsonTableSource"/>) — с путем к массиву записей.
///
/// Путь заведен ради приема больших файлов: раздел 8 роадмапа обещает «большие файлы —
/// существующим потоковым импортом», а конверт сообщения из папки обмена держит записи под
/// ключом <c>rows</c>, и до этого пути прочитать их потоком было нечем. Проверяется здесь именно
/// СПУСК: сами правила раскладки общие с остальными источниками и закрыты матрицей импорта.
///
/// Тесты файловые — источник работает поверх <c>FileStream</c>, и проверять его на строке
/// значило бы проверять другой класс.
/// </summary>
public sealed class JsonTableSourceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sbjson_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
    }

    private string File_(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
        return path;
    }

    private static (ImportSchema Schema, List<ImportRow> Rows) Read(JsonTableSource source)
    {
        var schema = source.Analyze(null, default);
        return (schema, [.. source.ReadRows(default)]);
    }

    /// <summary>Без пути все как было: записи — сам файл.</summary>
    [Fact]
    public void Without_a_path_the_file_itself_is_the_list()
    {
        var path = File_("bare.json", """[{"Название":"А-1","Сумма":100}]""");

        var (schema, rows) = Read(new JsonTableSource(path));

        Assert.Equal(1, schema.RowCount);
        Assert.Equal("А-1", rows[0].Title);
    }

    /// <summary>
    /// С путем записи берутся из названного поля, а поля конверта в раскладку НЕ попадают. Ради
    /// этого путь и нужен: конверт сообщения — это метаданные, а не колонка таблицы.
    /// </summary>
    [Fact]
    public void A_path_takes_the_rows_out_of_the_envelope()
    {
        var path = File_("envelope.json", """
            {
              "collection": "Заказы",
              "mode": "update",
              "key": "Номер",
              "rows": [
                {"Номер":"А-1","Сумма":100},
                {"Номер":"А-2","Сумма":250}
              ]
            }
            """);

        var (schema, rows) = Read(new JsonTableSource(path, "rows"));

        Assert.Equal(2, schema.RowCount);
        Assert.Equal(["А-1", "А-2"], rows.Select(r => r.Title).ToArray());

        // Ни «collection», ни «mode» колонками не стали.
        Assert.DoesNotContain("collection", schema.FieldNames);
        Assert.DoesNotContain("mode",       schema.FieldNames);
    }

    /// <summary>
    /// Массив записей может лежать ПОСЛЕ других полей и даже после вложенного объекта: спуск
    /// пропускает чужие поля целиком, каким бы ни было их содержимое.
    /// </summary>
    [Fact]
    public void Fields_before_the_rows_are_skipped_whole()
    {
        var path = File_("after.json", """
            {
              "meta": {"источник": "1С", "вложенный": {"а": [1, 2, 3]}},
              "список": ["мусор", "еще мусор"],
              "rows": [{"Номер":"А-9"}]
            }
            """);

        var (_, rows) = Read(new JsonTableSource(path, "rows"));

        Assert.Single(rows);
        Assert.Equal("А-9", rows[0].Title);
    }

    /// <summary>Путь с точками спускается по уровням — как у источника поверх строки.</summary>
    [Fact]
    public void A_dotted_path_goes_deeper()
    {
        var path = File_("deep.json", """
            {"data": {"шум": 1, "items": [{"Номер":"Б-1"}, {"Номер":"Б-2"}]}}
            """);

        var (_, rows) = Read(new JsonTableSource(path, "data.items"));

        Assert.Equal(["Б-1", "Б-2"], rows.Select(r => r.Title).ToArray());
    }

    /// <summary>
    /// Названного поля нет — ошибка СЛОВАМИ, называющая поле. Молчаливый ноль записей здесь
    /// худший из ответов: файл выглядел бы принятым и пустым.
    /// </summary>
    [Fact]
    public void A_missing_path_says_which_field_is_missing()
    {
        var path = File_("nokey.json", """{"collection":"Заказы","items":[{"а":1}]}""");

        var error = Assert.Throws<ImportException>(
            () => new JsonTableSource(path, "rows").Analyze(null, default));

        Assert.Contains("rows", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Путь есть, а в файле массив верхнего уровня — тоже отказ словами.</summary>
    [Fact]
    public void A_path_over_a_bare_array_is_refused()
    {
        var path = File_("bare2.json", """[{"Номер":"А-1"}]""");

        var error = Assert.Throws<ImportException>(
            () => new JsonTableSource(path, "rows").Analyze(null, default));

        Assert.Contains("rows", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Главное свойство пути — потоковость: файл, который не поместился бы в память несколько
    /// раз, читается построчно. Десять тысяч записей здесь не про скорость, а про то, что
    /// разбор идет поверх потока, а не поверх целиком прочитанной строки.
    /// </summary>
    [Fact]
    public void A_long_list_inside_an_envelope_is_read_streaming()
    {
        var body = string.Join(",",
            Enumerable.Range(1, 10_000).Select(i => $$"""{"Номер":"А-{{i}}","Сумма":{{i}}}"""));

        var path = File_("big.json", $$"""{"collection":"Заказы","rows":[{{body}}]}""");

        var source = new JsonTableSource(path, "rows");

        Assert.Equal(10_000, source.Analyze(null, default).RowCount);
        Assert.Equal(10_000, source.ReadRows(default).Count());
        Assert.Equal("А-10000", source.ReadRows(default).Last().Title);
    }
}
