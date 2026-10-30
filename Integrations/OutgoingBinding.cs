using System.Text.Encodings.Web;
using System.Text.Json;
using SmartBase.Core.Exporters;
using SmartBase.Core.Exporters.TableWriters;
using SmartBase.Core.Models;

namespace SmartBase.Integrations;

/// <summary>
/// Как обернуто тело исходящего запроса. Сторонние программы просят по-разному, и подогнать их под
/// один вид нельзя: обмен либо принимается той стороной, либо нет.
/// </summary>
public enum OutgoingShape
{
    /// <summary>Голый массив записей: <c>[{…},{…}]</c>.</summary>
    Array = 0,

    /// <summary>Массив внутри объекта: <c>{"orders":[{…}]}</c>.</summary>
    Wrapped,

    /// <summary>Одна запись объектом: <c>{…}</c>. Больше одной записи такой обмен не примет.</summary>
    Single,
}

/// <summary>Собранное тело запроса не помещается в одно сообщение транспорта.</summary>
internal sealed class OutgoingBodyLimitException() : InvalidOperationException(
    "тело запроса больше технического ограничения 32 МБ; сузьте отбор отправляемых данных")
{
}

/// <summary>
/// Одно поле, уходящее наружу.
/// </summary>
/// <param name="FieldId">Свойство коллекции; <c>null</c> — название записи.</param>
/// <param name="Name">Имя этого поля в JSON — так, как его ждет сторонняя программа. Оно почти
/// никогда не совпадает с названием свойства: у нас «Номер заказа», у них <c>order_no</c>.</param>
public sealed record OutgoingField(int? FieldId, string Name);

/// <summary>
/// Исходящая привязка — «какие поля коллекции и под какими именами уходят наружу». Зеркало
/// <see cref="CollectionBinding"/> (docs/ROADMAP-API.md, раздел 2: «Наружу — зеркально»).
///
/// Состав выгрузки задан явно: поле коллекции и имя в JSON стороннего сервиса.
///
/// Модель без ввода-вывода: строки читает репозиторий, тело собирает <see cref="OutgoingBody"/>.
/// </summary>
/// <param name="DbId">База, из которой берутся записи. Названа явно по той же причине, что и у
/// входящей привязки: соединения и запросы общие для всех баз, а ключи коллекций у каждой
/// свои.</param>
/// <param name="CollectionId">Коллекция, записи которой уходят.</param>
/// <param name="Fields">Поля и их имена в JSON. Порядок — тот, в каком они показаны человеку.</param>
/// <param name="Shape">Как обернуто тело.</param>
/// <param name="Envelope">Имя поля-обертки (только для <see cref="OutgoingShape.Wrapped"/>).</param>
/// <param name="Conditions">Какие записи уходят; пусто — вся коллекция. Условия названы по
/// свойствам коллекции — см. <see cref="Tests"/>.</param>
public sealed record OutgoingBinding(
    int                              DbId,
    int                              CollectionId,
    IReadOnlyList<OutgoingField>     Fields,
    OutgoingShape                    Shape      = OutgoingShape.Array,
    string                           Envelope   = "",
    IReadOnlyList<ScenarioCondition>? Conditions = null)
{
    /// <summary>Сколько условий отбора умещается в одной привязке.</summary>
    public const int MaxConditions = 10;

    /// <summary>Обертка объектом — у нее есть имя поля.</summary>
    public bool Wrapped => Shape == OutgoingShape.Wrapped;

    /// <summary>Обмен на одну запись: больше одной такой запрос не примет.</summary>
    public bool Single => Shape == OutgoingShape.Single;

    /// <summary>Условия отбора записей; пустой список означает всю коллекцию.</summary>
    public IReadOnlyList<ScenarioCondition> Tests => Conditions ?? [];

    /// <summary>
    /// Что не так с привязкой; пустой список — годится. Отказ, а не исключение: привязку
    /// собирает человек в окне, и неверно заполненное поле — обычное дело.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (DbId <= 0 || CollectionId <= 0)
            problems.Add("Не выбрана коллекция, записи которой уходят");

        if (Fields.Count == 0)
            problems.Add("Не выбрано ни одного поля — отправлять нечего");

        if (Fields.Any(f => string.IsNullOrWhiteSpace(f.Name)))
            problems.Add("У каждого поля должно быть имя, под которым оно уходит");

        // Два поля под одним именем — это не «последнее победит», а молчаливая потеря одного из
        // них на той стороне: JSON с повторяющимся ключом разбирают все по-своему.
        var twice = Fields.Select(f => f.Name.Trim())
                          .Where(n => n.Length > 0)
                          .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                          .Where(g => g.Count() > 1)
                          .Select(g => g.Key)
                          .ToList();

        if (twice.Count > 0)
            problems.Add("Одно имя занято дважды: " + string.Join(", ", twice));

        if (Wrapped && string.IsNullOrWhiteSpace(Envelope))
            problems.Add("Не задано имя поля, в котором уйдет массив записей");

        if (Tests.Count > MaxConditions)
            problems.Add($"Условий отбора больше {MaxConditions}: столько правил читаются хуже, "
                       + "чем отдельная коллекция");

        foreach (var test in Tests)
            if (test.Problems() is { Count: > 0 } bad)
                problems.Add($"Условие «{test}»: " + string.Join(", ", bad));

        return problems;
    }

    /// <summary>Как это читается на карточке запроса.</summary>
    public string Describe()
    {
        var body = Shape switch
        {
            OutgoingShape.Single  => $"одна запись, полей: {Fields.Count}",
            OutgoingShape.Wrapped => $"записи в поле «{Envelope.Trim()}», полей: {Fields.Count}",
            _                     => $"массив записей, полей: {Fields.Count}",
        };

        return Tests.Count == 0 ? body : body + $", отбор: {Tests.Count}";
    }

    /// <summary>Отбор одной строкой для окна и карточки; пусто — вся коллекция.</summary>
    public string DescribeTests() =>
        Tests.Count == 0 ? "" : string.Join(" и ", Tests.Select(t => t.ToString()));
}

/// <summary>
/// Готовое тело исходящего запроса вместе с тем, что о нем надо записать в журналы.
/// </summary>
/// <param name="Body">Тело как оно уйдет в сеть.</param>
/// <param name="Rows">Сколько записей в нем.</param>
/// <param name="Fields">Имена полей через запятую — для строки журнала безопасности. Значений
/// там нет и не будет, поэтому имена собираются отдельно от тела.</param>
/// <param name="Collection">Имя коллекции — для сообщений человеку.</param>
/// <param name="Body">Готовое тело запроса.</param>
/// <param name="Rows">Сколько записей в нем.</param>
/// <param name="Fields">Имена ушедших полей через запятую — для журналов.</param>
/// <param name="Collection">Из какой коллекции взято.</param>
/// <param name="Single">Значения ЕДИНСТВЕННОЙ записи «имя поля → текст»; <c>null</c> — записей
/// не одна. Нужны подстановкам <c>{{запись.id}}</c> и <c>{{поле.Имя}}</c> в ПУТИ запроса: путь
/// собирается один раз на запуск, поэтому подставить в него запись можно только тогда, когда она
/// одна (docs/ROADMAP-API.md, раздел 2). Ради этого же случая существует и форма «одна запись»:
/// <c>PUT /orders/123</c> привязкой к телу не выражается никак.</param>
public sealed record OutgoingPayload(string Body, int Rows, string Fields, string Collection,
                                     IReadOnlyDictionary<string, string>? Single = null);

/// <summary>
/// Сборка тела из строк коллекции.
///
/// <b>Значения типизируются по типу свойства, а не по виду значения.</b> Число уходит числом,
/// галочка — <c>true</c>/<c>false</c>, мультисписок — массивом строк, все остальное — строкой.
/// Ровно те же правила, что у выгрузки коллекции в JSON
/// (<see cref="JsonTableWriter"/>): два способа получить из одной коллекции JSON обязаны давать
/// одинаковый JSON. Угадывать тип по самому значению нельзя — артикул <c>007</c> ушел бы числом
/// <c>7</c>, и нашлось бы это через неделю по ненайденному товару.
///
/// Кириллица не превращается в <c>\uXXXX</c>: тело показывается человеку сухим прогоном, и
/// читать его должно быть можно.
/// </summary>
public static class OutgoingBody
{
    /// <summary>Счетчик точного размера строк JSON, подготовленный один раз на чтение коллекции.</summary>
    public static RowSizer CreateRowSizer(OutgoingBinding binding, IReadOnlyList<ExportColumn> columns) =>
        new(binding, columns);

    public sealed class RowSizer : IDisposable
    {
        private readonly IReadOnlyList<ExportColumn> _columns;
        private readonly JsonEncodedText[] _names;
        private readonly CountStream _stream = new();
        private readonly Utf8JsonWriter _writer;

        internal RowSizer(OutgoingBinding binding, IReadOnlyList<ExportColumn> columns)
        {
            _columns = columns;
            _names = binding.Fields.Select(f => JsonEncodedText.Encode(
                f.Name.Trim(), JavaScriptEncoder.UnsafeRelaxedJsonEscaping)).ToArray();
            _writer = new Utf8JsonWriter(_stream, new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }

        public long Measure(string[] row)
        {
            _stream.Count = 0;
            _writer.Reset(_stream);
            WriteRow(_writer, _names, _columns, row);
            _writer.Flush();
            return _stream.Count;
        }

        public void Dispose()
        {
            _writer.Dispose();
            _stream.Dispose();
        }

        private sealed class CountStream : Stream
        {
            public long Count { get; set; }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => Count;
            public override long Position { get => Count; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override void Write(byte[] buffer, int offset, int count) => Count += count;
            public override void Write(ReadOnlySpan<byte> buffer) => Count += buffer.Length;
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Собрать тело. <paramref name="columns"/> идут в том же порядке, что и
    /// <see cref="OutgoingBinding.Fields"/>, — их дает репозиторий, читающий строки: тип поля
    /// берется из коллекции на момент отправки, а не из сохраненной привязки, иначе смена типа
    /// свойства молча меняла бы вид выгрузки.
    /// </summary>
    /// <exception cref="ArgumentException">Списки полей и колонок разошлись — это ошибка
    /// вызывающего, а не человека за экраном.</exception>
    public static OutgoingPayload Build(OutgoingBinding             binding,
                                        IReadOnlyList<ExportColumn> columns,
                                        IReadOnlyList<string[]>     rows,
                                        string                      collection)
    {
        if (columns.Count != binding.Fields.Count)
            throw new ArgumentException(
                "Колонок не столько же, сколько полей привязки", nameof(columns));

        using var buffer = new BodyStream();
        var names  = binding.Fields
                            .Select(f => JsonEncodedText.Encode(
                                f.Name.Trim(), JavaScriptEncoder.UnsafeRelaxedJsonEscaping))
                            .ToArray();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
               {
                   Indented = false,
                   Encoder  = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
               }))
        {
            switch (binding.Shape)
            {
                case OutgoingShape.Single:
                    // Записей нет — уходит пустой объект, а не «ничего»: тело обязано остаться
                    // разбираемым JSON, иначе сторонняя программа ответит невнятной ошибкой разбора.
                    WriteRow(writer, names, columns, rows.Count > 0 ? rows[0] : []);
                    break;

                case OutgoingShape.Wrapped:
                    writer.WriteStartObject();
                    writer.WritePropertyName(JsonEncodedText.Encode(
                        binding.Envelope.Trim(), JavaScriptEncoder.UnsafeRelaxedJsonEscaping));
                    WriteRows(writer, names, columns, rows);
                    writer.WriteEndObject();
                    break;

                default:
                    WriteRows(writer, names, columns, rows);
                    break;
            }
        }

        return new OutgoingPayload(
            System.Text.Encoding.UTF8.GetString(buffer.ToArray()),
            binding.Single ? Math.Min(1, rows.Count) : rows.Count,
            string.Join(", ", binding.Fields.Select(f => f.Name.Trim())),
            collection,
            Only(binding, rows));
    }

    /// <summary>
    /// Значения единственной записи «имя поля → текст»; <c>null</c> — записей не одна.
    ///
    /// Ровно одна — это ровно одна: ни первая из трех, ни «возьмем какую-нибудь». Путь запроса
    /// собирается ОДИН раз на запуск, и подставить в него запись из трех значило бы отправить
    /// две оставшиеся по чужому адресу.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? Only(OutgoingBinding binding,
                                                             IReadOnlyList<string[]> rows)
    {
        if (rows.Count != 1) return null;

        var made = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var row  = rows[0];

        for (var i = 0; i < binding.Fields.Count && i < row.Length; i++)
            made[binding.Fields[i].Name.Trim()] = row[i] ?? "";

        return made;
    }

    private static void WriteRows(Utf8JsonWriter writer, JsonEncodedText[] names,
                                  IReadOnlyList<ExportColumn> columns,
                                  IReadOnlyList<string[]> rows)
    {
        writer.WriteStartArray();
        foreach (var row in rows)
        {
            WriteRow(writer, names, columns, row);
            writer.Flush();
        }
        writer.WriteEndArray();
    }

    /// <summary>Предел одного сообщения проверяется по мере записи JSON.</summary>
    private sealed class BodyStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }

        private void Check(int count)
        {
            if (Position + count > EgressHttp.MaxResponseBytes)
                throw new OutgoingBodyLimitException();
        }
    }

    /// <summary>
    /// Одна запись объектом. Значения короче списка полей — остальные уходят пустыми строками:
    /// пропустить ключ значило бы отдать той стороне разные наборы полей у разных записей.
    /// </summary>
    private static void WriteRow(Utf8JsonWriter writer, JsonEncodedText[] names,
                                 IReadOnlyList<ExportColumn> columns, string[] row)
    {
        writer.WriteStartObject();

        for (var i = 0; i < names.Length; i++)
        {
            var raw = i < row.Length ? row[i] ?? "" : "";

            switch (columns[i].FieldType)
            {
                case "checkbox":
                    writer.WriteBoolean(names[i], WriterHelpers.IsChecked(raw));
                    break;

                case "number" when raw.Length > 0 && WriterHelpers.TryNumber(raw, out var number):
                    writer.WriteNumber(names[i], number);
                    break;

                case "multiselect":
                    writer.WriteStartArray(names[i]);
                    foreach (var one in MultiSelectValue.Parse(raw)) writer.WriteStringValue(one);
                    writer.WriteEndArray();
                    break;

                default:
                    writer.WriteString(names[i], raw);
                    break;
            }
        }

        writer.WriteEndObject();
    }
}
