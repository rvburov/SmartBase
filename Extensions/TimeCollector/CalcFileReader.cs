using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using ExcelDataReader;
using Serilog;
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

    // Индексы колонок листа (0-based) = F(n+1) в SQL-запросе макроса.
    private const int SrcHierarchy = 1;   // F2  — иерархический номер, только условие отбора
    private const int SrcName      = 2;   // F3  — наименование
    private const int SrcDeno      = 3;   // F4  — обозначение КД
    private const int SrcNorm      = 6;   // F7  — трудоемкость

    /// <summary>Итог по файлу: собранные строки и, если есть, замечание для журнала.
    /// Замечание может стоять и у прочитанного файла — так помечается книга, открытая
    /// другим пользователем.</summary>
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
                owner is null ? null : new FileIssue(fileName, FileStatus.OpenedByOther, owner));
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
            var note = owner is null ? "" : $" ({owner})";
            return Problem(fileName, FileStatus.Locked, $"заблокирован другим процессом{note}");
        }
        catch (Exception ex)
        {
            // Причина (битый zip, чужой формат, обрезанный файл) иначе нигде бы не осталась.
            Log.Warning(ex, "Сводная трудоемкость: не удалось прочитать {File:l}", path);
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

    // Условие отбора 1-в-1 из TimeCollector.GetData: строка идет в свод, только если заполнены
    // И трудоемкость, И иерархический номер (последний в свод не переносится — он лишь признак
    // того, что строка описывает изделие, а не операцию).
    private static List<TimeRow> ReadRows(IExcelDataReader reader, string path, CalcFileName name,
                                          CancellationToken ct)
    {
        var rows  = new List<TimeRow>();
        var rowNo = 0;

        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            rowNo++;

            if (reader.FieldCount <= SrcNorm) continue;

            var (norm, normText) = ReadNorm(reader.GetValue(SrcNorm));
            if (norm is null && normText.Length == 0) continue;
            if (Text(reader.GetValue(SrcHierarchy)).Length == 0) continue;

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

    private static string Text(object? value) => value switch
    {
        null        => "",
        string s    => s,
        double d    => d.ToString(CultureInfo.CurrentCulture),
        DateTime dt => dt.ToString(CultureInfo.CurrentCulture),
        bool b      => b ? "ИСТИНА" : "ЛОЖЬ",
        _           => Convert.ToString(value, CultureInfo.CurrentCulture) ?? "",
    };

    /// <summary>Трудоемкость: число — числом, нечисловой текст — как есть (в макросе такое
    /// значение не переживало <c>CDec</c> и попадало в свод строкой).</summary>
    private static (double? Norm, string Text) ReadNorm(object? value)
    {
        switch (value)
        {
            case null:      return (null, "");
            case double d:  return (d, "");
            case int n:     return (n, "");
            case decimal m: return ((double)m, "");
        }

        var text = Text(value).Trim();
        if (text.Length == 0) return (null, "");

        if (double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out var parsed) ||
            double.TryParse(text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed))
            return (parsed, "");

        return (null, text);
    }

    // ── Кто держит книгу открытой ─────────────────────────────────────────────

    /// <summary>
    /// Пока книга открыта в Excel, рядом с ней лежит служебный файл «~$имя.xlsm» с именем
    /// владельца. Сам расчет при этом читается — поэтому файл не пропускается, а помечается
    /// в журнале: терять его строки хуже, чем показать данные на момент последнего сохранения.
    /// </summary>
    private static string? OwnerNote(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return null;

        var ownerPath = Path.Combine(dir, "~$" + Path.GetFileName(path));
        if (!File.Exists(ownerPath)) return null;

        var user = OwnerName(ownerPath);
        return user.Length > 0
            ? $"открыт пользователем {user} — данные прочитаны с диска"
            : "открыт другим пользователем — данные прочитаны с диска";
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
