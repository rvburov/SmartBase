using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using ExcelDataReader;
using SmartBase.Core.Importers.TableSources;

namespace SmartBase.Extensions.TimeCollector;

/// <summary>
/// Чтение одного файла расчета — потоково, через ExcelDataReader (книга в память не поднимается,
/// тот же стек, что у импорта). Заменяет связку ADO/ACE OLEDB из макроса: колонки <c>F1…F11</c>
/// запроса «SELECT … FROM [Расшифровка$]» — это колонки листа A…K, то есть индексы 0…10 ридера.
///
/// Вторая задача класса — назвать причину, по которой файл не прочитался: ради этого расширение
/// и заводилось (макрос про такие файлы сообщал лишь числом в итоговом MsgBox).
/// </summary>
internal static class CalcFileReader
{
    /// <summary>Лист, из которого макрос берет данные.</summary>
    private const string SourceSheet = "Расшифровка";

    /// <summary>Пометка вместо трудоемкости, которую нельзя записать числом (переполнение,
    /// ошибка формулы). Обозначение то же, что показывает сам Excel.</summary>
    private const string BadNumber = "#ЧИСЛО!";

    /// <summary>Строк шапки над данными листа — <c>top_indent</c> макросов расшифровки,
    /// данные у них начинаются со второй строки.</summary>
    private const int HeaderRows = 1;

    // Индексы колонок листа (0-based) = F(n+1) в SQL-запросе макроса.
    private const int SrcHierarchy = 1;   // F2  — иерархический номер, только условие отбора
    private const int SrcName      = 2;   // F3  — наименование
    private const int SrcDeno      = 3;   // F4  — обозначение КД
    private const int SrcNorm      = 6;   // F7  — трудоемкость

    /// <summary>Итог по файлу: собранные строки и, если есть, замечание для журнала.
    /// Замечание может стоять и у прочитанного файла — так помечается книга, открытая
    /// другим пользователем, и осиротевший рядом с ней «~$»-файл.</summary>
    internal sealed record FileReadResult(string FileName, IReadOnlyList<TimeRow> Rows, FileIssue? Issue);

    internal static FileReadResult Read(string path, CancellationToken ct)
    {
        var fileName = Path.GetFileName(path);

        var parsed = TimeCollectorEngine.ParseFileName(fileName);
        if (parsed is null)
            return Problem(fileName, FileStatus.BadName, "имя не по образцу «Проект_ГГГГ.ММ.ДД_ФИО.xlsm»");

        // Проверяем ДО открытия: если книга занята намертво, замечание попадет и в текст ошибки.
        var owner = OwnerNote(path);

        try
        {
            // ExcelDataReader лезет за кодировкой 1252 даже в OpenXml, а в .NET legacy-кодировок
            // из коробки нет: без регистрации провайдера каждый файл падал бы как «поврежден».
            // Program.Main зовет то же самое, но расширение не вправе на это рассчитывать —
            // движок работает и из тестового хоста.
            EncodingSetup.Ensure();

            // FileShare.ReadWrite — книгу, открытую в Excel, читаем без конфликта (тот же прием,
            // что в ImportErrors.OpenRead). Отказ приходит только при жесткой блокировке.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateOpenXmlReader(fs);

            if (!MoveToSheet(reader))
                return Problem(fileName, FileStatus.NoSheet, $"нет листа «{SourceSheet}»");

            var rows = ReadRows(reader, path, parsed, ct);
            if (rows.Count == 0)
                return Problem(fileName, FileStatus.NoRows,
                               "нет строк с заполненными иерархическим номером и трудоемкостью");

            return new FileReadResult(fileName, rows,
                owner is null ? null : new FileIssue(fileName, owner.Status, owner.Message));
        }
        catch (OperationCanceledException) { throw; }
        catch (FileNotFoundException)
        {
            return Problem(fileName, FileStatus.Corrupted, "файл исчез во время обхода папки");
        }
        catch (UnauthorizedAccessException)
        {
            return Problem(fileName, FileStatus.NoAccess, "нет прав доступа к файлу");
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 0x20 or 0x21)   // sharing/lock violation
        {
            var note = owner is null ? "" : $" ({owner.Message})";
            return Problem(fileName, FileStatus.Locked, $"заблокирован другим процессом{note}");
        }
        catch (Exception ex)
        {
            // Причина (битый zip, чужой формат, обрезанный файл) едет в журнал расширения
            // вместе с именем файла — больше ее взять неоткуда.
            return Problem(fileName, FileStatus.Corrupted,
                           $"поврежден или не является файлом Excel: {ex.Message}");
        }
    }

    // ── Лист и строки ─────────────────────────────────────────────────────────

    private static bool MoveToSheet(IExcelDataReader reader)
    {
        do
        {
            if (string.Equals(reader.Name, SourceSheet, StringComparison.OrdinalIgnoreCase)) return true;
        }
        while (reader.NextResult());
        return false;
    }

    // Условие отбора из TimeCollector.GetData: строка идет в свод, только если заполнены
    // И трудоемкость, И иерархический номер (последний в свод не переносится — он лишь признак
    // того, что строка описывает изделие, а не операцию).
    //
    // Шапку это условие не отсекает: подписи стоят и над номером, и над трудоемкостью. Макрос
    // шапку явно не пропускал — за него это делал драйвер ACE: без IMEX=1 он дает колонке тип
    // по большинству первых восьми строк, и текст подписи в числовой колонке G приходил пустым.
    // ExcelDataReader отдает ячейку как есть, поэтому шапка пропускается по номеру строки —
    // иначе подписи колонок приезжали в свод строкой из каждого расчета.
    private static List<TimeRow> ReadRows(IExcelDataReader reader, string path, CalcFileName name,
                                          CancellationToken ct)
    {
        var rows  = new List<TimeRow>();
        var rowNo = 0;

        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            rowNo++;

            if (rowNo <= HeaderRows) continue;
            if (reader.FieldCount <= SrcNorm) continue;

            // Иерархический номер проверяем ПЕРВЫМ, хотя условие то же самое (нужны оба
            // значения). Разница в цене отсева: пустой номер виден по самой ячейке, а разбор
            // трудоемкости собирает строку и пытается ее разобрать числом. Строк-операций
            // (у них номера нет) в расчетах подавляющее большинство — в свод из десяти тысяч
            // строк файла проходит порядка двух сотен.
            if (IsBlank(reader.GetValue(SrcHierarchy))) continue;

            var (norm, normText) = ReadNorm(reader.GetValue(SrcNorm));
            if (norm is null && normText.Length == 0) continue;

            rows.Add(new TimeRow(
                RNum:       rowNo - 1,                                   // см. TimeRow.RNum
                Name:       Text(reader.GetValue(SrcName)),
                Deno:       Text(reader.GetValue(SrcDeno)).Replace(" ", ""),
                Norm:       norm,
                NormText:   normText,
                Date:       name.Date,
                Efio:       name.Efio,
                Proj:       name.Proj,
                SourcePath: path));
        }

        return rows;
    }

    // ── Значения ячеек ────────────────────────────────────────────────────────

    /// <summary>Пуста ли ячейка. Отдельно от <see cref="Text"/>: тому ради одной проверки
    /// пришлось бы собрать строку — на десятках миллионов отсеиваемых строк это заметно.
    /// Ответы совпадают с <c>Text(value).Length == 0</c> для всех типов значений ридера.</summary>
    private static bool IsBlank(object? value) => value switch
    {
        null     => true,
        string s => s.Length == 0,
        _        => false,
    };

    private static string Text(object? value) => value switch
    {
        null        => "",
        string s    => s,
        double d    => d.ToString(CultureInfo.CurrentCulture),
        DateTime dt => dt.ToString(CultureInfo.CurrentCulture),
        bool b      => b ? "ИСТИНА" : "ЛОЖЬ",
        _           => Convert.ToString(value, CultureInfo.CurrentCulture) ?? "",
    };

    /// <summary>Трудоемкость: число — числом, нечисловой текст — как есть, чтобы непорядок
    /// в расчете был виден в своде. Макрос такие строки терял молча: драйвер ACE отдавал текст
    /// в числовой колонке пустым, и строка не проходила отбор. Отличие осознанное.
    ///
    /// Бесконечность и NaN числом НЕ считаются, хотя формально это <c>double</c>: такие
    /// значения приходят из ячеек с переполнением и ошибками формул, а в лист Excel они
    /// попадали как <c>&lt;v&gt;Infinity&lt;/v&gt;</c> — синтаксически XML цел, но Excel такую
    /// книгу объявляет нечитаемой и «восстанавливает», выбрасывая лист целиком. Ставим вместо
    /// значения пометку: строка свода уцелеет, а ошибку будет видно.
    ///
    /// Число приходит с точностью Excel — см. <see cref="WithoutBinaryNoise"/>.</summary>
    private static (double? Norm, string Text) ReadNorm(object? value)
    {
        switch (value)
        {
            case null:      return (null, "");
            case double d:  return double.IsFinite(d) ? (WithoutBinaryNoise(d), "") : (null, BadNumber);
            case int n:     return (n, "");
            case decimal m: return (WithoutBinaryNoise((double)m), "");
        }

        var text = Text(value).Trim();
        if (text.Length == 0) return (null, "");

        if (double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out var parsed) ||
            double.TryParse(text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed))
            return double.IsFinite(parsed) ? (WithoutBinaryNoise(parsed), "") : (null, BadNumber);

        return (null, text);
    }

    /// <summary>
    /// Значащих цифр, которые у трудоемкости могут быть НАСТОЯЩИМИ. Все, что дальше, —
    /// шум двоичного представления.
    ///
    /// Почему 12, а не 15 (предел точности самого Excel). Трудоемкость в расчете считается
    /// суммой: в реальной ячейке стоит что-нибудь вроде <c>=СУММ(G3:G11)+H12+H96+H97+…</c>
    /// на два десятка слагаемых, и каждое из них — тоже сумма. Погрешность на сложении
    /// НАКАПЛИВАЕТСЯ: на двух десятках слагаемых она добирается до 15-й значащей цифры,
    /// на сотнях — до 14-й. То есть за пределом, который прячет Excel: 0,850000000000001
    /// он показывает как есть.
    ///
    /// Двенадцать — с запасом с обеих сторон. Сверху: трудоемкости хватает девяти-десяти
    /// значащих цифр (1234567,891 часа — уже фантастика), так что настоящих данных здесь
    /// не теряется. Снизу: до 12-й цифры накопленная погрешность добралась бы только на
    /// тысячах операций сложения.
    /// </summary>
    private const int MeaningfulDigits = 12;

    /// <summary>
    /// Число без шума двоичного представления.
    ///
    /// Кэш формулы оседает в файле не тем числом, которое видит человек: вместо «6,1» там
    /// лежит 6,1000000000000005 — ДРУГОЕ число, у него другие биты. Excel разницу обычно
    /// прячет (он показывает 15 значащих цифр), а .NET — нет: <c>ToString</c> у <c>double</c>
    /// дает кратчайшую запись, по которой число восстанавливается точно, а для этого нужны
    /// все 17 цифр.
    ///
    /// Без округления одна причина била в трех местах сразу. В коллекции «6,1» показывалось
    /// как «6,1000000000000005» (формат «Обычное число» отдает значение как есть). В лист
    /// Excel уезжала та же длинная запись — там ее прятал формат колонки. И, главное,
    /// ломалось СРАВНЕНИЕ: <c>PlanColors</c> сверяет трудоемкости точно, и строки
    /// с одинаковым на вид значением объявлялись расходящимися и красились красным —
    /// в обоих приемниках, потому что раскраска считается один раз до записи.
    /// </summary>
    private static double WithoutBinaryNoise(double value)
    {
        var rounded = double.Parse(value.ToString("G" + MeaningfulDigits, CultureInfo.InvariantCulture),
                                   NumberStyles.Float, CultureInfo.InvariantCulture);

        // У чисел под самым пределом double округление вверх дает бесконечность. Для
        // трудоемкости это фантастика, но превратить число в «нечитаемый лист» страховка
        // дешевле, чем потом искать причину.
        return double.IsFinite(rounded) ? rounded : value;
    }

    // ── Кто держит книгу открытой ─────────────────────────────────────────────

    /// <summary>Что показала проверка «~$»-файла: занята книга сейчас или маркер осиротел.</summary>
    private sealed record OwnerCheck(FileStatus Status, string Message);

    /// <summary>
    /// Пока книга открыта в Excel, рядом с ней лежит служебный файл «~$имя.xlsm» с именем
    /// владельца. Сам расчет при этом читается — поэтому файл не пропускается, а помечается
    /// в журнале: терять его строки хуже, чем показать данные на момент последнего сохранения.
    ///
    /// Но одного маркера мало: он остается лежать после аварийного закрытия Excel и приезжает
    /// вместе с копией папки (файл скрытый, копируется молча). Такой «~$» ни о чем не говорит,
    /// а замечание по нему пугает — на скопированной папке их набирались десятки. Поэтому
    /// найденный маркер ПОДТВЕРЖДАЕТСЯ пробой: <see cref="IsBusy"/> пытается открыть книгу
    /// монопольно. Не вышло — книга правда занята; вышло — маркер осиротел
    /// (<see cref="FileStatus.StaleLock"/>, в журнале одна строка со счетчиком).
    ///
    /// Проба идет ТОЛЬКО при найденном маркере: на 3 тыс. файлов их единицы, и лишнего
    /// открытия на каждый расчет не случается.
    /// </summary>
    private static OwnerCheck? OwnerNote(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return null;

        var ownerPath = Path.Combine(dir, "~$" + Path.GetFileName(path));
        if (!File.Exists(ownerPath)) return null;

        var user = OwnerName(ownerPath);
        var who  = user.Length > 0 ? $" ({user})" : "";

        if (!IsBusy(path))
            return new OwnerCheck(FileStatus.StaleLock,
                $"книга не открыта, рядом остался служебный файл «~$» от прошлого сеанса{who}");

        return new OwnerCheck(FileStatus.OpenedByOther, user.Length > 0
            ? $"открыт пользователем {user} — данные прочитаны с диска"
            : "открыт другим пользователем — данные прочитаны с диска");
    }

    /// <summary>
    /// Держит ли книгу кто-то прямо сейчас. Проверяется попыткой открыть ее МОНОПОЛЬНО
    /// (<c>FileShare.None</c>): открытый в Excel файл дает на этом «нарушение совместного
    /// доступа» (0x20/0x21), свободный открывается. Захват длится доли миллисекунды и сразу
    /// отпускается — читаем мы потом как раньше, с <c>FileShare.ReadWrite</c>.
    ///
    /// Любая другая ошибка (нет прав, файл исчез) — это НЕ ответ на вопрос «занят ли»:
    /// считаем книгу занятой, то есть остаемся при прежнем, осторожном поведении. Настоящую
    /// причину все равно назовет основное чтение следом.
    /// </summary>
    private static bool IsBusy(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 0x20 or 0x21)
        {
            return true;
        }
        catch
        {
            return true;
        }
    }

    // Формат «~$»-файла недокументирован: первый байт — длина имени, дальше само имя.
    // Разбор best-effort: не получилось — вернем пустую строку и напишем в журнал без имени.
    private static string OwnerName(string ownerPath)
    {
        try
        {
            using var fs = new FileStream(ownerPath, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[128];
            var read = fs.Read(head, 0, head.Length);
            if (read < 2) return "";

            int length = head[0];
            if (length <= 0 || length + 1 > read) return "";

            return Sanitize(Decode(head, 1, length));
        }
        catch
        {
            return "";
        }
    }

    private static string Decode(byte[] bytes, int offset, int length)
    {
        // Имя пишется однобайтовой кодировкой ОС; провайдер кодовых страниц регистрирует
        // Program.Main, но если его нет — не падаем, а читаем как Latin1.
        try   { return Encoding.GetEncoding(1251).GetString(bytes, offset, length); }
        catch { return Encoding.Latin1.GetString(bytes, offset, length); }
    }

    private static string Sanitize(string raw)
    {
        var clean = new string(raw.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return clean.Length is > 0 and <= 64 ? clean : "";
    }

    private static FileReadResult Problem(string fileName, FileStatus status, string message) =>
        new(fileName, [], new FileIssue(fileName, status, message));
}
