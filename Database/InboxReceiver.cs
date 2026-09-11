using SmartBase.Core.Importers;
using SmartBase.Core.Importers.TableSources;
using SmartBase.Database.Repositories;
using SmartBase.Integrations;

namespace SmartBase.Database;

/// <summary>
/// Чем кончился обход папки.
/// </summary>
/// <param name="Accepted">Файлов принято.</param>
/// <param name="Rejected">Файлов отклонено — по каждому лежит квитанция с причиной.</param>
/// <param name="Repeats">Файлов оказалось повторами: сообщение с таким идентификатором уже
/// приходило, и второй раз оно не обрабатывается.</param>
/// <param name="Reclaimed">Брошенных захватов возвращено во «Входящие».</param>
/// <param name="Receipts">Квитанции этого обхода — то же, что уехало отправителю.</param>
/// <param name="Problem">Почему обход не состоялся вовсе: папка вне политики, пропал сетевой
/// диск, база сейчас недоступна. Пусто — обход прошел. Поле нужно ЭКРАНУ: молчаливое «новых
/// файлов нет» в ответ на недоступную папку — ровно то, из-за чего потом неделю ищут, почему
/// обмен «не работает».</param>
public sealed record InboxRun(
    int                          Accepted,
    int                          Rejected,
    int                          Repeats,
    int                          Reclaimed,
    IReadOnlyList<InboxReceipt>  Receipts,
    string                       Problem = "")
{
    public static readonly InboxRun Nothing = new(0, 0, 0, 0, []);

    /// <summary>Обход не состоялся — причина в <see cref="Problem"/>.</summary>
    public static InboxRun Failed(string problem) => new(0, 0, 0, 0, [], problem);

    /// <summary>Сколько файлов тронуто всего.</summary>
    public int Files => Accepted + Rejected + Repeats;

    /// <summary>Итог одной строкой — для экрана и журнала.</summary>
    public string Summary()
    {
        if (Problem.Length > 0) return "обход не состоялся: " + Problem;

        var parts = new List<string>();

        if (Accepted  > 0) parts.Add($"принято {Accepted}");
        if (Rejected  > 0) parts.Add($"отклонено {Rejected}");
        if (Repeats   > 0) parts.Add($"повторов {Repeats}");
        if (Reclaimed > 0) parts.Add($"возвращено брошенных {Reclaimed}");

        return parts.Count == 0 ? "новых файлов нет" : string.Join(", ", parts);
    }
}

/// <summary>
/// Приемник: обходит папку обмена и укладывает каждый готовый файл в коллекцию.
///
/// Здесь сходятся три слоя, каждый из которых сам по себе ничего не знает про остальные:
/// <see cref="InboxFolder"/> ведет файлы и захваты, <see cref="InboxMessage"/> разбирает формат,
/// <c>ConstructorRepository</c> сводит сообщение с коллекцией и пишет записи. Разделение
/// намеренное: файловый протокол проверяется на настоящих файлах без базы, формат — вообще без
/// ввода-вывода, а укладка — тем же кодом, что и ответ чужой программы.
///
/// <b>Порядок шагов для одного файла и есть содержание этого класса:</b>
///
/// <list type="number">
///   <item><b>Захват.</b> Не удался — файл уже у соседа, идем дальше без единой записи.</item>
///   <item><b>Разбор и проверка формата.</b> Негодный файл получает квитанцию с причиной и
///   уезжает в «Ошибки»: молчание в ответ на присланный файл — худшее, чем может кончиться
///   обмен.</item>
///   <item><b>Проверка на повтор.</b> Есть квитанция с таким идентификатором — файл уезжает в
///   «Обработанные», а прежняя квитанция остается нетронутой: отправитель, перечитав ее,
///   получит тот же ответ, что и в первый раз.</item>
///   <item><b>Укладка.</b> Обычным путем записи, на отдельном подключении.</item>
///   <item><b>Квитанция, и только потом перенос файла.</b> Упади программа между укладкой и
///   переносом — файл найдется снова, но квитанция уже лежит, и повтор отсечется по ней.</item>
/// </list>
/// </summary>
public static class InboxReceiver
{
    /// <summary>
    /// Обойти папку один раз. Права проверяет укладка — приемник работает от лица того, кто
    /// сидит за машиной, а не от имени какого-то особого пользователя: чужая запись честно
    /// попадет в историю изменений, и админ отзовет доступ той же панелью, что и человеку.
    /// </summary>
    /// <param name="layout">Разложенные папки обмена.</param>
    /// <param name="repo">Репозиторий целевой базы.</param>
    /// <param name="dbId">Ключ этой базы — привязка обязана называть базу явно.</param>
    /// <param name="machine">Имя машины для метки захвата.</param>
    /// <param name="now">Текущий момент: помесячная папка и отметки в квитанциях.</param>
    /// <param name="ct">Отмена; уже начатый файл доводится до квитанции.</param>
    /// <param name="progress">Имя файла, за который взялись.</param>
    public static InboxRun Run(InboxLayout       layout,
                               ConstructorRepository repo,
                               int               dbId,
                               string            machine,
                               DateTimeOffset    now,
                               CancellationToken ct = default,
                               IProgress<string>? progress = null)
    {
        var reclaimed = InboxFolder.ReclaimStale(layout, now);
        var ready     = InboxFolder.Ready(layout, now);

        var receipts = new List<InboxReceipt>();
        var accepted = 0;
        var rejected = 0;
        var repeats  = 0;

        foreach (var file in ready)
        {
            if (ct.IsCancellationRequested) break;

            var claimed = InboxFolder.TryClaim(file, machine, now);
            if (claimed is null) continue;   // забрал сосед

            progress?.Report(file.Name);

            InboxReceipt receipt;
            bool         repeat;

            try
            {
                (receipt, repeat) = Handle(layout, repo, dbId, file, claimed, now);
            }
            catch (InboxException failure)
            {
                // Ответить не вышло (папка «Квитанции» пропала, нет прав на запись). Файл
                // остается захваченным и никуда не едет: перенести его, не сумев ответить,
                // значило бы потерять и файл, и ответ разом. Захват вернется во «Входящие»
                // сам, а обход продолжается — валить его из-за одного файла незачем.
                Serilog.Log.Warning("Приемник: «{File:l}» отложен — {Reason:l}",
                                    file.Name, failure.Message);
                continue;
            }

            receipts.Add(receipt);
            if      (repeat)           repeats++;
            else if (receipt.Accepted) accepted++;
            else                       rejected++;

            // Строка журнала на каждый файл — включая отклоненный: разбор «почему записи не
            // появились» начинается именно с них. В накопительные квоты обмен файлами не
            // засчитывается: квоты ограничивают то, что уходит наружу, а файл идет внутрь.
            RunJournal.Add(RunEntry.New(
                RunChannel.Inbox, file.Name, layout.Incoming,
                receipt.Accepted, repeat ? "повтор: сообщение уже приходило" : receipt.Explanation,
                receipt.Created + receipt.Updated, 0, TimeSpan.Zero,
                dbId, now, counts: false, direction: 0));   // файл идет внутрь
        }

        return new InboxRun(accepted, rejected, repeats, reclaimed, receipts);
    }

    // ── Один файл ─────────────────────────────────────────────────────────────

    private static (InboxReceipt Receipt, bool Repeat) Handle(
        InboxLayout layout, ConstructorRepository repo, int dbId,
        InboxFile file, string claimed, DateTimeOffset now)
    {
        // Потолок размера спрашивается ДО чтения: файл в общую папку кладет кто угодно с
        // правами на запись, и гигабайт оттуда не должен кончаться нехваткой памяти.
        var text = InboxFolder.ReadText(claimed, out var unread);

        if (text is null)
            return (Reject(layout, file, claimed, "", "", 0, unread, now), false);

        var message = InboxMessage.TryParse(text, out var error);

        if (message is null)
            return (Reject(layout, file, claimed, "", "", 0, error, now), false);

        var problems = message.Problems();
        if (problems.Count > 0)
            return (Reject(layout, file, claimed, message.MessageId, message.Collection,
                           message.Rows, string.Join("; ", problems), now), false);

        // Повтор: прежняя квитанция НЕ переписывается — отправитель, перечитав ее, обязан
        // получить тот же ответ, что и в первый раз. Файл при этом убирается из «Входящих»,
        // иначе он остался бы там навсегда и каждый обход спотыкался бы о него заново.
        if (InboxFolder.AlreadySeen(layout, message.MessageId))
        {
            var past = InboxFolder.PastReceipt(layout, message.MessageId)
                    ?? InboxReceipt.Accept(file.Name, message.MessageId, message.Collection,
                                           message.Rows, BindingResult.Nothing, now,
                                           "повтор: сообщение уже приходило");

            Move(layout, claimed, file, accepted: true, now);
            return (past, true);
        }

        // Записи читает ТОТ ЖЕ источник, что и ответ чужой программы: один и тот же набор
        // данных, пришедший файлом и по сети, обязан ложиться в коллекцию одинаково.
        var source = new JsonTextSource(message.RowsJson);

        ImportSchema schema;
        try
        {
            schema = source.Analyze(null, CancellationToken.None);
        }
        catch (ImportException failure)
        {
            return (Reject(layout, file, claimed, message.MessageId, message.Collection,
                           message.Rows, failure.Message, now), false);
        }

        // Свод и укладка идут на ОТДЕЛЬНОМ подключении: обход папки целиком живет в фоне, а
        // общий контекст в это время занят интерфейсом.
        var plan = repo.TryPlanIsolated(message, schema, dbId, out var problem);

        if (plan is null)
            return (Reject(layout, file, claimed, message.MessageId, message.Collection,
                           message.Rows, problem, now), false);

        BindingResult result;
        try
        {
            result = repo.ApplyRowsIsolated(plan.Binding, schema,
                                            source.ReadRows(CancellationToken.None),
                                            CancellationToken.None);
        }
        catch (Exception failure) when (failure is ImportException or InvalidOperationException)
        {
            return (Reject(layout, file, claimed, message.MessageId, message.Collection,
                           message.Rows, failure.Message, now), false);
        }

        // Про выброшенные колонки отправитель узнает из квитанции. Чужая программа отдает
        // лишнее почти всегда, и это не ошибка; но молча выбросить присланное — худшее, что
        // может сделать приемник: отправитель годами будет думать, что колонка доходит.
        var note = plan.Ignored.Count > 0
            ? "не перенесены колонки: " + string.Join(", ", plan.Ignored)
            : "";

        var receipt = InboxReceipt.Accept(file.Name, message.MessageId, message.Collection,
                                          message.Rows, result, now, note);

        Write(layout, receipt);
        Move(layout, claimed, file, accepted: true, now);

        return (receipt, false);
    }

    // ── Ответ и уборка ────────────────────────────────────────────────────────

    private static InboxReceipt Reject(InboxLayout layout, InboxFile file, string claimed,
                                       string id, string collection, int rows,
                                       string explanation, DateTimeOffset now)
    {
        var receipt = InboxReceipt.Rejected(file.Name, id, collection, rows, explanation, now);

        Write(layout, receipt);
        Move(layout, claimed, file, accepted: false, now);

        return receipt;
    }

    /// <summary>
    /// Записать квитанцию. Не записалась — <see cref="InboxException"/> уходит наверх, и файл
    /// остается захваченным: перенести его, не сумев ответить, значило бы потерять и файл, и
    /// ответ разом. Обходом это ловится и откладывается до следующего раза.
    /// </summary>
    private static void Write(InboxLayout layout, InboxReceipt receipt) =>
        InboxFolder.WriteReceipt(layout, receipt);

    private static void Move(InboxLayout layout, string claimed, InboxFile file,
                             bool accepted, DateTimeOffset now)
    {
        try
        {
            InboxFolder.Finish(layout, claimed, file, accepted, now);
        }
        catch (InboxException failure)
        {
            // Квитанция уже лежит, значит повтор отсечется по ней; файл заберет следующий
            // обход и увидит его повтором. Валить весь обход из-за одного файла незачем.
            Serilog.Log.Warning("Приемник: «{File:l}» не убрался из «Входящих» — {Reason:l}",
                                file.Name, failure.Message);
        }
    }
}
