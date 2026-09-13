using System.Globalization;
using System.Security.Cryptography;
using SmartBase.Core.Models;
using SmartBase.Utils;

namespace SmartBase.Database;

/// <summary>
/// Одна выгруженная коллекция внутри файла.
/// </summary>
/// <param name="DbId">База, из которой взяты записи.</param>
/// <param name="Collection">Имя коллекции — так, как его видит человек.</param>
/// <param name="Rows">Сколько записей ушло.</param>
/// <param name="Columns">Имена выгруженных столбцов. Только имена: значений в журнале
/// безопасности нет и не будет.</param>
/// <param name="Label">Самая строгая метка среди базы и выгруженных столбцов.</param>
public sealed record ExportedCollection(
    int                   DbId,
    string                Collection,
    long                  Rows,
    IReadOnlyList<string> Columns,
    ConfidentialityLabel  Label);

/// <summary>
/// След выгрузки в файл в журнале безопасности (docs/ROADMAP-API.md, шаг 6) — ручной и, с
/// шага 11, автоматической: шаг сценария «экспорт выборки» оставляет ту же строку, только с
/// именем сценария.
///
/// Замок из шага 5 стоит на сетевой двери, но данные уходят и другим путем: человек жмет
/// «Экспорт» и получает файл со всей коллекцией. Функция рабочая, и запрещать ее нельзя —
/// выгрузка в Excel и есть половина ежедневной работы. Но проверяющий, разбирающий утечку,
/// обязан увидеть ее в том же журнале, где видит отправки: иначе самый простой способ вынести
/// базу оказывается единственным, не оставляющим следа.
///
/// <b>Строка на файл, а не на коллекцию.</b> Наружу уходит файл — его уносят на флешке, его
/// прикладывают к письму, по его отпечатку сверяют «тот ли это файл». Книга с листом на
/// коллекцию дала бы три строки об одном предмете, и объем пришлось бы или утроить, или делить
/// между листами — оба ответа неверные.
///
/// <b>Правило «не записалось — не отправляем» переносится с оговоркой.</b> Журнал спрашивается
/// ДО первого созданного файла (<see cref="Ready"/>), а строка пишется ПОСЛЕ: пока файл не
/// дописан, неизвестны ни объем, ни отпечаток, а дописать строку задним числом нельзя — цепочка
/// хэшей правок не знает. Если след все же не лег, готовый файл удаляется: выгрузка, о которой
/// не осталось записи, не допускается ровно так же, как отправка.
///
/// <b>Закрытая база выгрузку не запрещает.</b> Запрет на исходящие (раздел 10.7) — про сетевую
/// дверь; отнять у закрытой базы экспорт значило бы отнять печать отчетов. Метка при этом
/// попадает в пояснение строки: выгрузка из базы «Закрытый контур» — первое, что проверяющий
/// ищет глазами.
/// </summary>
public static class ExportTrace
{
    /// <summary>
    /// Сколько имен столбцов попадает в строку журнала. Та же причина и то же число, что у
    /// <see cref="OutwardGuard.MaxFields"/>: строка журнала — то, что читают глазами, а
    /// коллекция бывает и на двести свойств.
    /// </summary>
    public const int MaxFields = 40;

    /// <summary>
    /// Готов ли журнал принять след. Спрашивается ДО начала выгрузки — узнать об отказе, когда
    /// файл уже лежит на диске, поздно.
    /// </summary>
    public static bool Ready => SecurityJournal.Current is not null;

    /// <summary>Почему выгрузка не начнется — готовой фразой для окна.</summary>
    public const string NoJournal =
        "Журнал безопасности не открылся, и выгрузка отменена: файл с данными, о котором не "
        + "осталось записи, не допускается. Сообщите ответственному за интеграции.";

    /// <summary>Почему готовый файл удален — готовой фразой для окна.</summary>
    public const string NotWritten =
        "След о выгрузке не лег в журнал безопасности, и файл удален: выгрузка, о которой не "
        + "осталось записи, не допускается. Сообщите ответственному за интеграции.";

    /// <summary>
    /// Записать след о созданном файле. <c>false</c> — следа не осталось, и вызывающий обязан
    /// убрать файл.
    /// </summary>
    /// <param name="path">Куда лег файл — полным путем: проверяющему нужно именно место.</param>
    /// <param name="tables">Что попало в файл.</param>
    /// <param name="now">Момент. Передается явно — журнал проверяется тестами построчно.</param>
    /// <param name="scenario">Имя сценария, если выгрузил шаг «экспорт выборки», — выгрузку,
    /// ушедшую ночью саму собой, проверяющий должен уметь привязать к тому, кто ее завел.
    /// Пусто — человек нажал «Экспорт» сам.</param>
    public static bool Note(string path, IReadOnlyList<ExportedCollection> tables,
                            DateTimeOffset now, string scenario = "")
    {
        var (bytes, hash) = Measure(path);

        return SecurityJournal.Add(SecurityEntry.New(
            SecurityEvent.Export,
            allowed:     true,
            who:         CurrentUser.DisplayName,
            scenario:    scenario,
            address:     path,
            fields:      Fields(tables),
            rows:        Rows(tables),
            bytes:       bytes,
            bodyHash:    hash,
            explanation: Explanation(tables, scenario),
            dbId:        SingleDb(tables),
            at:          now));
    }

    // ── Что пишем в строку ────────────────────────────────────────────────────

    /// <summary>
    /// Объем и отпечаток готового файла. Отпечаток — единственный способ ответить на вопрос
    /// «тот ли это файл»: журнал хранит имена столбцов, но не содержимое, и сверить принесенную
    /// флешку с записью иначе нечем.
    ///
    /// Файл читается целиком еще раз, и это сознательно: выгрузка перед этим прочитала всю
    /// коллекцию страницами и закодировала ее в xlsx — на этом фоне один последовательный
    /// проход не стоит ничего.
    ///
    /// Не прочитался — строка все равно пишется, без отпечатка. След важнее отпечатка: потерять
    /// запись о выгрузке из-за занятого антивирусом файла было бы обменом наоборот.
    /// </summary>
    private static (long Bytes, string Hash) Measure(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            var       hash = Convert.ToHexStringLower(SHA256.HashData(file));

            return (file.Length, hash);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning("Отпечаток выгруженного файла не снят: {Path:l} — {Reason:l}",
                                path, failure.Message);
            return (0, "");
        }
    }

    /// <summary>Сколько записей ушло файлом — по всем его листам разом.</summary>
    private static int Rows(IReadOnlyList<ExportedCollection> tables) =>
        (int)Math.Min(int.MaxValue, tables.Sum(t => t.Rows));

    /// <summary>
    /// Имена столбцов через запятую, без повторов. Книга с несколькими листами дает их общим
    /// списком: все перечисленное ушло одним файлом, а какой лист чей — сказано пояснением.
    /// </summary>
    private static string Fields(IReadOnlyList<ExportedCollection> tables)
    {
        var names = tables.SelectMany(t => t.Columns)
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .ToList();

        return names.Count <= MaxFields
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(MaxFields)) + $" и еще {names.Count - MaxFields}";
    }

    /// <summary>
    /// Пояснение: что за коллекции и сколько строк каждая, а перед ними — метка, если она
    /// строже «Открыто». Метка идет первой намеренно: ее ищут взглядом, а не вычитывают.
    /// </summary>
    private static string Explanation(IReadOnlyList<ExportedCollection> tables,
                                      string scenario = "")
    {
        var label = tables.Aggregate(ConfidentialityLabel.Open,
                                     (strictest, table) =>
                                         Confidentiality.Strictest(strictest, table.Label));

        // Число записей — в скобках за именем коллекции: сколько их всего, и так стоит
        // отдельной колонкой журнала, а склонять «запись» в трех формах ради пояснения незачем.
        var what = string.Join(", ", tables.Select(
            t => $"«{t.Collection}» ({t.Rows.ToString("N0", CultureInfo.CurrentCulture)})"));

        var mark = label == ConfidentialityLabel.Open
            ? ""
            : $"{Confidentiality.Title(label)}. ";

        var how = scenario.Length > 0 ? $"выгружено сценарием «{scenario}»" : "выгружено руками";

        return $"{mark}{how}: {what}";
    }

    /// <summary>
    /// Ключ базы для строки журнала; 0 — баз несколько. Книга, собранная из разных баз, не
    /// принадлежит ни одной, и назвать первую из них значило бы соврать отбору по базе.
    /// </summary>
    private static int SingleDb(IReadOnlyList<ExportedCollection> tables)
    {
        var ids = tables.Select(t => t.DbId).Distinct().ToList();

        return ids.Count == 1 ? ids[0] : 0;
    }
}
