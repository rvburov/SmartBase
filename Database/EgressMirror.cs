using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SmartBase.Integrations;

namespace SmartBase.Database;

/// <summary>
/// Зеркало исходящего — копия того, что ушло наружу, в папке аудита организации
/// (docs/ROADMAP-API.md, 10.1: защита от сотрудника, выносящего данные через «легальную»
/// интеграцию).
///
/// <b>Чем зеркало отличается от журнала безопасности — и почему их двое.</b> В журнале данных
/// нет и не будет: его выгружают проверяющему целиком, и значение поля в нем стало бы второй
/// утечкой следом за первой. Но вопрос «что именно ушло» этим не закрывается: хэш тела отвечает
/// «то же или другое», а не «что там было». Зеркало отвечает на второй вопрос — и потому лежит
/// НЕ у программы, а в папке, которую называет организация и к которой доступ есть у
/// проверяющего, а не у того, за кем ведется наблюдение.
///
/// <b>По умолчанию зеркала нет.</b> Папку называет политика полем <c>mirror</c>; не назвала —
/// копии не ведутся. Это то же правило, что и у остального контура: отсутствие настройки
/// означает безопасное состояние, а здесь безопасное — не разбрасывать копии секретных данных по
/// машине.
///
/// <b>Зеркало не замок.</b> Правило «не записалось — не отправляем» живет у журнала безопасности
/// и остается там: журнал спрашивается ДО сокета и может отменить отправку. Копия же снимается
/// ПОСЛЕ — только по итогу известно, ушло ли и сколько, — и отменять ею уже случившуюся отправку
/// нечем. Неудача записи не глушится: она уходит в «Диагностику», а состояние зеркала показано в
/// паспорте интеграций.
/// </summary>
public static class EgressMirror
{
    /// <summary>Ведется ли зеркало: политика назвала хотя бы одну папку.</summary>
    public static bool Configured => EgressGate.Policy.Mirrors.Count > 0;

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        // Кириллица остается кириллицей: копию читает человек, а «Заказы» не читает никто.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Снять копию отправленного. Возвращает, сколько копий легло; 0 — зеркало не ведется либо
    /// ни одна папка не приняла запись.
    ///
    /// Зовется только для того, что ДОШЛО до сети: копия неотправленного запроса в папке аудита
    /// означала бы отправку, которой не было, и разбираться с ней пришлось бы дольше, чем с
    /// настоящей.
    /// </summary>
    /// <param name="request">Запрос, который отправляли.</param>
    /// <param name="safeAddress">Адрес БЕЗ секрета — тот же, что в журналах.</param>
    /// <param name="body">Тело, как оно ушло.</param>
    /// <param name="rows">Сколько записей ушло; 0 — тело набрано руками.</param>
    /// <param name="fields">Какие поля ушли, через запятую.</param>
    /// <param name="ok">Чем кончилась отправка.</param>
    /// <param name="who">Кто отправлял.</param>
    /// <param name="scenario">Имя сценария; пусто — нажали кнопку.</param>
    /// <param name="now">Момент.</param>
    public static int Note(IntegrationRequest request, string safeAddress, string body,
                           int rows, string fields, bool ok, string who, string scenario,
                           DateTimeOffset now)
    {
        var folders = EgressGate.Policy.Mirrors;
        if (folders.Count == 0) return 0;

        var text = JsonSerializer.Serialize(new
        {
            at       = now.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            machine  = Environment.MachineName,
            who,
            scenario,
            request  = request.Name,
            method   = request.Method.ToString().ToUpperInvariant(),
            address  = safeAddress,
            ok,
            rows,
            fields,
            bodyHash = SecurityJournal.Sha256(body ?? ""),
            body     = body ?? "",
        }, Format);

        var name  = FileName(request, now);
        var wrote = 0;

        foreach (var folder in folders)
            if (Write(folder, name, text, now)) wrote++;

        return wrote;
    }

    /// <summary>
    /// Имя файла копии: момент, имя запроса и короткий отпечаток. Момент впереди, чтобы папка
    /// читалась по порядку; отпечаток в конце — чтобы две отправки одной секунды не легли одна
    /// на другую.
    /// </summary>
    private static string FileName(IntegrationRequest request, DateTimeOffset now)
    {
        var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var mark  = Guid.NewGuid().ToString("N")[..6];

        return $"{stamp}-{Safe(request.Name)}-{mark}.json";
    }

    /// <summary>Имя запроса, годное в имени файла на любой системе.</summary>
    private static string Safe(string name)
    {
        var clean = new StringBuilder(name.Length);

        foreach (var symbol in name.Trim())
            clean.Append(Path.GetInvalidFileNameChars().Contains(symbol) ? '_' : symbol);

        var text = clean.ToString();
        return text.Length == 0 ? "запрос" : text[..Math.Min(40, text.Length)];
    }

    /// <summary>
    /// Записать копию в одну папку. Раскладка помесячная — как у «Обработанных» в папке обмена:
    /// в одной папке за год накапливаются десятки тысяч файлов, и открыть ее становится нельзя.
    /// </summary>
    private static bool Write(string root, string name, string text, DateTimeOffset now)
    {
        try
        {
            var month = Path.Combine(root, now.ToString("yyyy-MM", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(month);

            // Сначала временное имя, потом переименование: тот же прием, что у папки обмена.
            // Читающий копии не должен наткнуться на недописанный файл.
            var final   = Path.Combine(month, name);
            var partial = final + ".tmp";

            File.WriteAllText(partial, text, new UTF8Encoding(false));
            File.Move(partial, final, overwrite: true);

            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
                                                or NotSupportedException or ArgumentException)
        {
            // Молчать нельзя: организация завела зеркало намеренно, и «копии не ведутся, а
            // никто не знает» — худший из исходов. Отправку при этом не отменяем: она уже
            // состоялась, а замком служит журнал безопасности.
            Serilog.Log.Warning("Зеркало исходящего: копия не легла в {Root:l} — {Reason:l}",
                                root, failure.Message);
            return false;
        }
    }
}
