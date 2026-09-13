using System.Text;
using System.Text.Json;
using SmartBase.Core.Importers;
using SmartBase.Core.Importers.TableSources;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Курсор по JSON-потоку (<see cref="JsonCursor"/>) — общий обход токенов для файлового источника
/// импорта и разбора конверта входящего сообщения.
///
/// Главное здесь — <see cref="JsonCursor.Skip"/>: именно он позволяет пройти мимо массива записей,
/// не втащив его в память, и именно поэтому у входящего файла больше нет потолка размера. Пока
/// пропуск делался через «прочитать значение и выбросить», пропустить гигабайтный массив было
/// нельзя — он сначала оказывался в памяти целиком.
/// </summary>
public sealed class JsonCursorTests
{
    private static JsonCursor Over(string json) =>
        new(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    // ── Пропуск ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Пропуск массива возвращает число его прямых элементов. Счет достается даром — отдельным
    /// проходом по файлу за «сколько записей в сообщении» платить не пришлось бы.
    /// </summary>
    [Fact]
    public void Skipping_an_array_counts_its_items()
    {
        var cursor = Over("""[{"а":1},{"б":2},{"в":3}]""");

        Assert.Equal(3, cursor.Skip());
        Assert.Equal(JsonTokenType.None, cursor.Peek());
    }

    /// <summary>Вложенность счет не путает: считаются ПРЯМЫЕ элементы, а не все объекты внутри.</summary>
    [Fact]
    public void Nesting_does_not_confuse_the_count()
    {
        var cursor = Over("""[{"а":[1,2,3],"б":{"в":{"г":[4,5]}}},{"д":6}]""");

        Assert.Equal(2, cursor.Skip());
    }

    /// <summary>Пустой массив — ноль, и это не то же самое, что «массива нет».</summary>
    [Fact]
    public void An_empty_array_is_zero()
    {
        Assert.Equal(0, Over("[]").Skip());
    }

    /// <summary>Скаляр и объект пропускаются тоже; элементов у них нет.</summary>
    [Fact]
    public void Scalars_and_objects_skip_as_one_value()
    {
        Assert.Equal(0, Over("\"текст\"").Skip());
        Assert.Equal(0, Over("42").Skip());
        Assert.Equal(0, Over("""{"а":1,"б":[1,2]}""").Skip());
    }

    /// <summary>
    /// После пропуска курсор стоит РОВНО за пропущенным значением. Промахнись он на токен — и
    /// поле конверта, написанное после массива записей, читалось бы как мусор.
    /// </summary>
    [Fact]
    public void After_a_skip_the_cursor_stands_right_after_the_value()
    {
        var cursor = Over("""{"rows":[{"а":1},{"б":2}],"id":"м-7"}""");

        Assert.Equal(JsonTokenType.StartObject, cursor.Peek());
        cursor.Consume();

        Assert.Equal("rows", cursor.TakeName());
        Assert.Equal(2, cursor.Skip());

        Assert.Equal(JsonTokenType.PropertyName, cursor.Peek());
        Assert.Equal("id", cursor.TakeName());

        using var value = cursor.Take();
        Assert.Equal("м-7", value.RootElement.GetString());
    }

    // ── Спуск по пути ─────────────────────────────────────────────────────────

    /// <summary>Спуск находит нужное поле, минуя чужие.</summary>
    [Fact]
    public void Descending_walks_past_foreign_fields()
    {
        var cursor = Over("""{"мета":{"кто":"1С"},"data":{"items":[{"а":1}]},"хвост":7}""");

        cursor.Descend("data.items");

        Assert.Equal(JsonTokenType.StartArray, cursor.Peek());
        Assert.Equal(1, cursor.Skip());
    }

    /// <summary>Пустой путь оставляет курсор на месте — записями считается сам поток.</summary>
    [Fact]
    public void An_empty_path_leaves_the_cursor_where_it_was()
    {
        var cursor = Over("""[{"а":1}]""");

        cursor.Descend("");

        Assert.Equal(JsonTokenType.StartArray, cursor.Peek());
    }

    /// <summary>Нет такого поля — понятный отказ, а не развал.</summary>
    [Fact]
    public void A_missing_segment_is_explained()
    {
        var cursor = Over("""{"мета":1}""");

        var failure = Assert.Throws<ImportException>(() => cursor.Descend("rows"));
        Assert.Contains("rows", failure.Message, StringComparison.Ordinal);
    }

    // ── Развал ────────────────────────────────────────────────────────────────

    /// <summary>Оборванный посреди значения поток — развал, а не тихий конец данных.</summary>
    [Fact]
    public void A_truncated_value_is_broken()
    {
        var cursor = Over("""{"а": "незакрытая стро""");
        cursor.Consume();                 // {
        Assert.Equal("а", cursor.TakeName());

        Assert.Throws<ImportException>(() => cursor.Take());
    }

    /// <summary>Оборванный массив при пропуске — тоже развал: иначе половина записей сошла бы за все.</summary>
    [Fact]
    public void A_truncated_array_is_broken_on_skip()
    {
        var cursor = Over("""[{"а":1},{"б":2}""");

        Assert.Throws<ImportException>(() => cursor.Skip());
    }

    // ── Буфер ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Значение длиннее начального буфера (64 КБ) читается целиком: буфер дорастает под ОДНО
    /// значение. Без этого длинное описание в одной записи роняло бы разбор всего файла.
    /// </summary>
    [Fact]
    public void A_value_longer_than_the_buffer_is_read_whole()
    {
        var long_ = new string('щ', 200_000);
        var cursor = Over($$"""{"текст":"{{long_}}"}""");

        cursor.Consume();
        Assert.Equal("текст", cursor.TakeName());

        using var value = cursor.Take();
        Assert.Equal(long_, value.RootElement.GetString());
    }

    /// <summary>
    /// Массив на сто тысяч элементов пропускается и считается. Проверка не про скорость, а про
    /// то, что пропуск идет токенами: собери он документ — сюда пришлось бы поместить весь
    /// массив, и ровно эта разница снимает потолок с входящего файла.
    /// </summary>
    [Fact]
    public void A_hundred_thousand_items_skip_without_being_held()
    {
        var json = new StringBuilder("[");
        for (var i = 0; i < 100_000; i++)
        {
            if (i > 0) json.Append(',');
            json.Append($$"""{"Номер":"А-{{i}}","Статус":"новый"}""");
        }
        json.Append(']');

        Assert.Equal(100_000, Over(json.ToString()).Skip());
    }

    /// <summary>Метка порядка байт снимается: ее кладут и Windows, и 1С.</summary>
    [Fact]
    public void A_byte_order_mark_is_not_data()
    {
        var cursor = new JsonCursor(new MemoryStream(
            [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("""[{"а":1}]""")]));

        Assert.Equal(1, cursor.Skip());
    }
}
