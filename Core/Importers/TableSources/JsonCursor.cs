using System.Text.Json;

namespace SmartBase.Core.Importers.TableSources;

/// <summary>
/// Курсор по JSON-потоку: читает файл токен за токеном, не складывая его в память.
///
/// <b>Зачем отдельный класс.</b> <see cref="Utf8JsonReader"/> — <c>ref struct</c>: он не переживает
/// ни <c>yield</c>, ни поле класса, ни границу метода-итератора. Поэтому потоковое чтение всегда
/// выглядит одинаково — сохраненное <see cref="JsonReaderState"/> плюс буфер, который дозаполняется
/// и дорастает, а ридер пересоздается на каждый шаг. Этот кусок нужен уже дважды: файловому
/// источнику импорта (<see cref="JsonTableSource"/>) и разбору конверта входящего сообщения
/// (<c>InboxMessage.TryParseFile</c>). Второй его копией стало бы второе понимание того, где в
/// файле кончается значение, — и они разошлись бы на первом же кривом файле.
///
/// <b>Память — O(одного значения), а не O(файла).</b> Буфер начинается с 64 КБ и растет только
/// затем, чтобы в него поместилось самое длинное ОДНО значение. Поэтому файл на десять гигабайт
/// читается теми же несколькими мегабайтами памяти, что и файл на десять килобайт, и именно это
/// снимает потолок на размер входящего файла.
///
/// Курсор ничего не знает ни про формат сообщения, ни про схему импорта: он умеет только
/// «посмотреть следующий токен», «съесть токен», «взять одно значение целиком» и «пропустить одно
/// значение, не читая его в память».
/// </summary>
public sealed class JsonCursor
{
    /// <summary>
    /// Потолок на ОДНО значение. Это не потолок файла: файл может быть любым, а вот одна строка
    /// или один объект длиной больше 64 МБ — почти всегда не данные, а сбой на стороне
    /// отправителя. Держать под такое значение буфер вдвое больше значило бы позволить одному
    /// испорченному файлу выбрать всю память машины.
    /// </summary>
    public const int MaxValueBytes = 64 * 1024 * 1024;

    private readonly Stream            _stream;
    private readonly JsonReaderOptions _options;

    private byte[]          _buffer = new byte[64 * 1024];
    private int             _start;
    private int             _length;
    private bool            _final;
    private JsonReaderState _state;

    /// <summary>
    /// Встать в начало потока. Метка порядка байт (BOM) снимается здесь: ее кладут и Windows, и
    /// учетные системы, а <see cref="Utf8JsonReader"/> считает ее мусором перед первым токеном.
    /// </summary>
    public JsonCursor(Stream stream)
    {
        _stream  = stream;
        _options = new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling     = JsonCommentHandling.Skip,
        };
        _state = new JsonReaderState(_options);

        Fill();

        if (_length >= 3 && _buffer[0] == 0xEF && _buffer[1] == 0xBB && _buffer[2] == 0xBF)
            _start = 3;
    }

    /// <summary>
    /// Сколько байт потока уже разобрано — прочитанное минус необработанный остаток буфера. Нужно
    /// показу хода работы: доля считается по байтам файла, а не по числу записей, потому что
    /// числа записей до конца чтения никто не знает.
    /// </summary>
    public long Position => _stream.Position - (_length - _start);

    /// <summary>
    /// Текст про испорченный файл — одна формулировка на все места, где обнаруживается развал.
    /// Вынесен в константу, потому что его СРАВНИВАЮТ: разбор входящего сообщения говорит
    /// отправителю о развале своими словами («файл не является JSON»), а не нашими, и узнать
    /// этот случай ему надо не по литералу, повторенному в двух файлах.
    /// </summary>
    public const string BrokenText = "Файл поврежден или имеет некорректную структуру JSON.";

    /// <summary>Испорченный файл.</summary>
    public static ImportException Broken() => new(BrokenText);

    // ── Шаги ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Тип следующего токена, НЕ сдвигая позицию. <see cref="JsonTokenType.None"/> — данные
    /// кончились.
    /// </summary>
    public JsonTokenType Peek()
    {
        while (true)
        {
            // Хвост из одних пробелов и переводов строк: свежий ридер (NDJSON) на таком остатке
            // при isFinalBlock кидает исключение вместо false — а это просто конец данных.
            if (_final && WhitespaceOnly()) return JsonTokenType.None;

            var reader = new Utf8JsonReader(_buffer.AsSpan(_start, _length - _start), _final, _state);

            try
            {
                if (reader.Read()) return reader.TokenType;
            }
            catch (JsonException) { throw Broken(); }

            if (_final) return JsonTokenType.None;
            Fill();
        }
    }

    /// <summary>Съесть один токен.</summary>
    public void Consume()
    {
        while (true)
        {
            var reader = new Utf8JsonReader(_buffer.AsSpan(_start, _length - _start), _final, _state);
            bool ok;

            try { ok = reader.Read(); }
            catch (JsonException) { throw Broken(); }

            if (ok)
            {
                _state  = reader.CurrentState;
                _start += (int)reader.BytesConsumed;
                return;
            }

            if (_final) throw Broken();
            Fill();
        }
    }

    /// <summary>
    /// Взять одно полное значение — объект, массив или скаляр — как <see cref="JsonDocument"/>.
    /// Вызывающий обязан его освободить: документ держит свою копию байт.
    /// </summary>
    public JsonDocument Take()
    {
        while (true)
        {
            var reader = new Utf8JsonReader(_buffer.AsSpan(_start, _length - _start), _final, _state);

            try
            {
                // Сначала встать НА первый токен значения тем же ридером: TryParseValue по
                // возобновленному состоянию принял бы «текущим» уже съеденный чужой токен
                // (StartArray внешнего массива) и распарсил бы не тот диапазон.
                if (reader.Read() && JsonDocument.TryParseValue(ref reader, out var document))
                {
                    _state  = reader.CurrentState;
                    _start += (int)reader.BytesConsumed;
                    return document;
                }
            }
            catch (JsonException) { throw Broken(); }

            if (_final) throw Broken();   // файл оборвался посреди значения
            Fill();
        }
    }

    /// <summary>
    /// Имя текущего свойства (сдвигает позицию). Отдельно от <see cref="Consume"/>, потому что имя
    /// надо сравнить: и спуск по пути, и разбор конверта идут по именам, а не по порядку полей.
    /// </summary>
    public string TakeName()
    {
        while (true)
        {
            var reader = new Utf8JsonReader(_buffer.AsSpan(_start, _length - _start), _final, _state);
            bool    ok;
            string? name = null;

            try
            {
                ok = reader.Read();
                if (ok) name = reader.GetString();
            }
            catch (JsonException) { throw Broken(); }

            if (ok)
            {
                _state  = reader.CurrentState;
                _start += (int)reader.BytesConsumed;
                return name ?? "";
            }

            if (_final) throw Broken();
            Fill();
        }
    }

    /// <summary>
    /// Пропустить одно значение целиком, НЕ читая его в память, и вернуть число прямых элементов,
    /// если это был массив (иначе 0).
    ///
    /// Отличие от <c>Take().Dispose()</c> принципиальное и ради него метод и написан: <c>Take</c>
    /// собирает документ, то есть кладет пропускаемое значение в память целиком. Массив записей на
    /// гигабайт так пропустить нельзя — а пропускать его приходится, потому что поля конверта
    /// отправитель вправе написать ПОСЛЕ записей.
    ///
    /// Заодно счет элементов достается даром: он нужен квитанции («принято 12 000 записей») и
    /// отказу «в сообщении нет записей», и отдельным проходом по файлу за него платить не пришлось.
    ///
    /// Рекурсия ограничена глубиной вложенности JSON, а ее ограничивает сам
    /// <see cref="Utf8JsonReader"/> (64 уровня по умолчанию) — глубже он не пустит.
    /// </summary>
    public long Skip()
    {
        var first = Peek();

        if (first == JsonTokenType.StartArray)
        {
            Consume();
            long items = 0;

            while (true)
            {
                var token = Peek();

                if (token == JsonTokenType.EndArray) { Consume(); return items; }
                if (token == JsonTokenType.None) throw Broken();

                Skip();
                items++;
            }
        }

        if (first == JsonTokenType.StartObject)
        {
            Consume();

            while (true)
            {
                var token = Peek();

                if (token == JsonTokenType.EndObject) { Consume(); return 0; }
                if (token != JsonTokenType.PropertyName) throw Broken();

                Consume();   // имя
                Skip();      // значение
            }
        }

        if (first == JsonTokenType.None) throw Broken();

        Consume();
        return 0;
    }

    /// <summary>
    /// Спуститься к значению по пути из имен, разделенных точками: <c>rows</c>, <c>data.items</c>.
    /// Пустой путь — остаться на месте. Поля, встреченные до нужного, пропускаются
    /// <see cref="Skip"/>, то есть тоже не попадают в память.
    /// </summary>
    public void Descend(string? path)
    {
        foreach (var segment in (path ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Peek() != JsonTokenType.StartObject)
                throw new ImportException($"Некорректный JSON: ожидался объект с полем «{segment}».");

            Consume();

            while (true)
            {
                var token = Peek();

                if (token is JsonTokenType.EndObject or JsonTokenType.None)
                    throw new ImportException($"В файле нет поля «{segment}» — записи не найдены.");

                if (token != JsonTokenType.PropertyName) throw Broken();

                if (string.Equals(TakeName(), segment, StringComparison.Ordinal)) break;

                Skip();   // чужое поле мимо — целиком, но не в память
            }
        }
    }

    /// <summary>
    /// Забыть состояние разбора. Нужно ровно для NDJSON: это последовательность НЕЗАВИСИМЫХ
    /// верхнеуровневых значений, и без сброса ридер после первого из них считает следующее
    /// «лишним токеном».
    /// </summary>
    public void Restart() => _state = new JsonReaderState(_options);

    // ── Буфер ─────────────────────────────────────────────────────────────────

    private void Fill()
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _length - _start);
            _length -= _start;
            _start   = 0;
        }

        if (_length == _buffer.Length)
        {
            if (_buffer.Length >= MaxValueBytes)
                throw new ImportException(
                    "Слишком большое значение в JSON (более 64 МБ) — файл не может быть импортирован.");

            Array.Resize(ref _buffer, _buffer.Length * 2);
        }

        var read = _stream.Read(_buffer, _length, _buffer.Length - _length);
        if (read == 0) _final = true;
        _length += read;
    }

    private bool WhitespaceOnly()
    {
        for (var i = _start; i < _length; i++)
            if (_buffer[i] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) return false;

        return true;
    }
}
