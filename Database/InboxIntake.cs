using SmartBase.Core.Importers;
using SmartBase.Core.Importers.TableSources;
using SmartBase.Database.Repositories;
using SmartBase.Integrations;

namespace SmartBase.Database;

/// <summary>
/// Укладка входящего сообщения в коллекцию — <b>общая часть обоих транспортов</b>.
///
/// Файл из папки обмена и тело HTTP-запроса приходят разными путями, но дальше обязаны идти
/// одним и тем же кодом (docs/ROADMAP-API.md, раздел 7): две реализации одной укладки разошлись
/// бы на первой же правке, и сообщение, принятое папкой, стало бы отклоняться по сети — или, что
/// хуже, наоборот.
///
/// Что остается у транспорта и сюда не попадает: чтение (файл или сокет), <b>проверка формата</b>
/// (<see cref="InboxMessage.Problems"/> спрашивается ДО памяти о повторах — негодное сообщение не
/// должно попадать в нее вовсе) и сама память о повторах: у папки это квитанция рядом с файлом,
/// у сети — своя таблица. Разница не случайная, она из 8-го раздела плана: вторая доставка файла
/// вполне может попасть на СОСЕДНЮЮ машину, а второе обращение по <c>127.0.0.1</c> — никогда.
/// </summary>
public static class InboxIntake
{
    /// <summary>
    /// Уложить записи сообщения и ответить квитанцией. Исключений не бросает: отказ — это такой
    /// же ответ отправителю, как и принятие, и молчание вместо него хуже любой причины.
    /// </summary>
    /// <param name="message">Разобранное и проверенное сообщение.</param>
    /// <param name="repo">Репозиторий целевой базы.</param>
    /// <param name="dbId">Ключ базы — привязка обязана называть базу явно.</param>
    /// <param name="source">Чем подписать квитанцию: имя файла либо имя канала.</param>
    /// <param name="now">Момент обработки.</param>
    /// <param name="beat">Знак жизни — зовется по ходу укладки каждые
    /// <see cref="BeatEvery"/> строк. Нужен файловому каналу: пока идет долгая укладка, захват
    /// файла обязан выглядеть живым, иначе сосед сочтет его брошенным. У сетевого канала захвата
    /// нет, и там это <c>null</c>.</param>
    public static InboxReceipt Apply(InboxMessage message, ConstructorRepository repo, int dbId,
                                     string source, DateTimeOffset now, Action? beat = null)
    {
        // Режимы без укладки — своими путями: у «удалить» нет привязки, у «подготовить
        // коллекцию» нет и строк. Общее у всех одно — квитанция.
        if (message.Mode == InboxMode.Delete)
            return Erase(message, repo, source, now, beat);

        if (message.Mode == InboxMode.EnsureCollection)
            return Ensure(message, repo, source, now);

        var reader = Reader(message);

        ImportSchema schema;
        try
        {
            schema = reader.Analyze(null, CancellationToken.None);
        }
        catch (ImportException failure)
        {
            return Reject(message, source, failure.Message, now);
        }

        // Свод и укладка идут на ОТДЕЛЬНОМ подключении: и обход папки, и обработка обращения
        // живут в фоне, а общий контекст в это время занят интерфейсом.
        var plan = repo.TryPlanIsolated(message, schema, dbId, out var problem);

        if (plan is null) return Reject(message, source, problem, now);

        BindingResult result;
        try
        {
            result = repo.ApplyRowsIsolated(plan.Binding, schema,
                                            Beating(reader.ReadRows(CancellationToken.None), beat),
                                            CancellationToken.None);
        }
        catch (Exception failure) when (failure is ImportException or InvalidOperationException)
        {
            return Reject(message, source, failure.Message, now);
        }

        // Про выброшенные колонки отправитель узнает из квитанции. Чужая программа отдает
        // лишнее почти всегда, и это не ошибка; но молча выбросить присланное — худшее, что
        // может сделать приемник: отправитель годами будет думать, что колонка доходит.
        var note = plan.Ignored.Count > 0
            ? "не перенесены колонки: " + string.Join(", ", plan.Ignored)
            : "";

        return InboxReceipt.Accept(source, message.MessageId, message.Collection,
                                   message.Rows, result, now, note);
    }

    // ── Удаление по ключу ─────────────────────────────────────────────────────

    /// <summary>
    /// Найти по ключу и удалить (docs/ROADMAP-API.md, шаг 11). Право «Удаление записи»
    /// спрашивает репозиторий на входе; повтор сообщения безвреден — второй раз удалять нечего,
    /// и квитанция честно скажет «пропущено».
    /// </summary>
    private static InboxReceipt Erase(InboxMessage message, ConstructorRepository repo,
                                      string source, DateTimeOffset now, Action? beat)
    {
        var reader = Reader(message);

        ImportSchema schema;
        try
        {
            schema = reader.Analyze(null, CancellationToken.None);
        }
        catch (ImportException failure)
        {
            return Reject(message, source, failure.Message, now);
        }

        ConstructorRepository.InboxErase? erased;
        string problem;
        try
        {
            erased = repo.EraseRowsIsolated(message, schema,
                                            Beating(reader.ReadRows(CancellationToken.None), beat),
                                            out problem);
        }
        catch (Exception failure) when (failure is ImportException or InvalidOperationException)
        {
            return Reject(message, source, failure.Message, now);
        }

        if (erased is null) return Reject(message, source, problem, now);

        var notes = new List<string>();

        if (erased.Missing   > 0) notes.Add($"не нашлось записей по {erased.Missing} ключам — "
                                          + "возможно, они уже удалены");
        if (erased.Ambiguous > 0) notes.Add($"по {erased.Ambiguous} ключам в коллекции несколько "
                                          + "записей — не тронута ни одна");

        return InboxReceipt.Erased(source, message.MessageId, message.Collection, message.Rows,
                                   erased.Deleted, erased.Missing, erased.Ambiguous, erased.Empty,
                                   now, string.Join("; ", notes));
    }

    // ── Подготовка коллекции ──────────────────────────────────────────────────

    /// <summary>
    /// Создать коллекцию по схеме и добрать недостающие поля (docs/ROADMAP-API.md, шаг 11).
    /// Права «Создание коллекции» и «Создание свойства» спрашивает репозиторий на входе.
    /// </summary>
    private static InboxReceipt Ensure(InboxMessage message, ConstructorRepository repo,
                                       string source, DateTimeOffset now)
    {
        ConstructorRepository.InboxEnsure? ensured;
        string problem;
        try
        {
            ensured = repo.EnsureCollectionIsolated(message, out problem);
        }
        catch (Exception failure) when (failure is InvalidOperationException)
        {
            return Reject(message, source, failure.Message, now);
        }

        if (ensured is null) return Reject(message, source, problem, now);

        var parts = new List<string>
        {
            ensured.CreatedCollection
                ? $"коллекция «{message.Collection}» создана"
                : $"коллекция «{message.Collection}» уже есть",
        };

        if (ensured.CreatedFields.Count > 0)
            parts.Add("полей добавлено: " + ensured.CreatedFields.Count
                    + " (" + string.Join(", ", ensured.CreatedFields) + ")");

        if (ensured.ExistingFields > 0)
            parts.Add($"полей уже было: {ensured.ExistingFields}");

        return InboxReceipt.Ensured(source, message.MessageId, message.Collection,
                                    string.Join(", ", parts), now);
    }

    // ── Знак жизни ────────────────────────────────────────────────────────────

    /// <summary>
    /// Через сколько строк подавать знак жизни. Отметка стоит одного обращения к файловой
    /// системе, а срок брошенности захвата — четыре часа: десять тысяч строк не проходят и за
    /// минуту даже на медленной базе, так что запас тут четырехзначный, и мельчить незачем.
    /// </summary>
    public const int BeatEvery = 10_000;

    /// <summary>
    /// Тот же поток строк, но каждые <see cref="BeatEvery"/> строк подается знак жизни.
    ///
    /// Обертка вокруг строк, а не отдельный поток или таймер, — намеренно: знак жизни обязан
    /// означать «работа ИДЕТ», а не «программа жива». Таймер отмечал бы захват живым и тогда,
    /// когда укладка намертво встала на замке базы, — то есть ровно в том случае, ради которого
    /// возврат брошенных захватов и написан.
    /// </summary>
    private static IEnumerable<ImportRow> Beating(IEnumerable<ImportRow> rows, Action? beat)
    {
        if (beat is null) return rows;

        return Counted(rows, beat);

        static IEnumerable<ImportRow> Counted(IEnumerable<ImportRow> rows, Action beat)
        {
            var done = 0;

            foreach (var row in rows)
            {
                if (++done % BeatEvery == 0) beat();
                yield return row;
            }
        }
    }

    // ── Откуда читаются записи ────────────────────────────────────────────────

    /// <summary>
    /// Чем читать записи сообщения. Источник ОДИН и тот же, что читает ответ чужой программы:
    /// один и тот же набор данных, пришедший файлом и по сети, обязан ложиться в коллекцию
    /// одинаково.
    ///
    /// Развилка — только в том, откуда берутся байты, и она не про удобство. Тело HTTP-обращения
    /// уже в памяти и уже обрезано потолком двери — читать его потоком неоткуда. Файл из общей
    /// папки кладет кто угодно и какого угодно размера, поэтому его записи остаются В ФАЙЛЕ и
    /// читаются потоково: память не зависит от размера файла, и потолок на него не нужен.
    /// </summary>
    private static ITableSource Reader(InboxMessage message) =>
        message.RowsFile.Length > 0
            ? new JsonTableSource(message.RowsFile, "rows")
            : new JsonTextSource(message.RowsJson);

    private static InboxReceipt Reject(InboxMessage message, string source, string explanation,
                                       DateTimeOffset now) =>
        InboxReceipt.Rejected(source, message.MessageId, message.Collection, message.Rows,
                              explanation, now);
}
