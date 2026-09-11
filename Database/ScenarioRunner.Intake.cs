using SmartBase.Integrations;

namespace SmartBase.Database;

/// <summary>
/// Шаг «файл из папки» — точка входа сценария (docs/ROADMAP-API.md, раздел 9).
///
/// <b>Точка входа принадлежит сценарию, а не расширению.</b> Папку выбирают при настройке шага,
/// поэтому одно расширение вешается на несколько папок, а одну папку разбирают разные сценарии.
/// Принадлежи она расширению — два расширения не поделили бы одну папку.
///
/// Файловая работа не пишется заново: протокол папки уже есть (<see cref="InboxFolder"/>) —
/// выдержка, монопольное открытие, захват переименованием, уборка брошенных захватов, квитанция
/// как память о повторах. Отличий от службы приема ровно два, и оба следуют из того, что здесь
/// работает СЦЕНАРИЙ, а не готовый разборщик формата.
///
/// <list type="number">
///   <item><b>Берется любой файл, а не только <c>.json</c>.</b> Формат задает шаг-преобразователь,
///   который стоит следующим: сообщение приходит и текстом, и XML, и чем угодно еще. Приемнику
///   же чужой формат брать незачем — он все равно отверг бы его.</item>
///   <item><b>За один запуск берется ОДИН файл.</b> Единица работы сценария — сообщение целиком:
///   один файл, одна цепочка шагов, одна квитанция. Обойти пачку значило бы смешать строки
///   разных отправителей в одной таблице и ответить им одним письмом.</item>
/// </list>
///
/// Пустая папка — не событие: шаг заканчивает сценарий ТИХО, без строки в журнале. Проверка
/// каждые пять минут иначе оставила бы к утру три сотни строк «делать было нечего».
/// </summary>
public static partial class ScenarioRunner
{
    /// <summary>Взятый файл, который ждет ответа: его судьба решается по итогу всего сценария.</summary>
    /// <param name="Layout">Папки обмена под выбранным корнем.</param>
    /// <param name="File">Исходный файл — его имя попадает в квитанцию.</param>
    /// <param name="Claimed">Путь захваченного файла (с меткой машины).</param>
    /// <param name="Id">Идентификатор сообщения; пусто — защиты от повтора нет.</param>
    private sealed record Intake(InboxLayout Layout, InboxFile File, string Claimed, string Id);

    /// <summary>
    /// Взять из папки один файл и передать его текст дальше. Возвращает
    /// <see cref="StepOutcome.Nothing"/>, если брать нечего.
    /// </summary>
    private static StepOutcome IntakeStep(ScenarioStep step, DateTimeOffset now, ref Intake? taken)
    {
        // Второй такой шаг взял бы второй файл, а квитанция у сценария одна: на два файла ее не
        // разделить, и один отправитель остался бы без ответа.
        if (taken is not null)
            return StepOutcome.Bad(
                "в сценарии два шага «файл из папки», а ответ отправителю один: разделите "
                + "сценарий на два");

        var layout = InboxFolder.Open(step.Folder, out var explanation);
        if (layout is null) return StepOutcome.Bad("папка обмена: " + explanation);

        // Захваты, брошенные выключенной посреди работы машиной, возвращаются во «Входящие» —
        // иначе файл уехал бы вместе с ней навсегда, а отправитель не узнал бы почему.
        InboxFolder.ReclaimStale(layout, now);

        foreach (var file in InboxFolder.Ready(layout, now, extension: ""))
        {
            var id = MessageId(file);

            if (InboxFolder.AlreadySeen(layout, id))
            {
                Repeat(layout, file, now);
                return StepOutcome.Nothing;
            }

            var claimed = InboxFolder.TryClaim(file, Environment.MachineName, now);
            if (claimed is null) continue;   // забрал сосед — идем к следующему

            // Читается тем же кодом, что и у службы приема, — вместе с потолком размера:
            // канал другой, а папка та же, и правила у нее одни.
            var text = InboxFolder.ReadText(claimed, out var unread);

            if (text is null)
            {
                // Прочитать не вышло — отвечаем отправителю и убираем файл в «Ошибки»: молча
                // оставить его во «Входящих» значило бы спотыкаться о него каждые пять минут.
                taken = new Intake(layout, file, claimed, id);
                return StepOutcome.Bad(unread);
            }

            taken = new Intake(layout, file, claimed, id);

            return new StepOutcome(true, $"Файл «{file.Name}» взят из папки", Text: text);
        }

        return StepOutcome.Nothing;
    }

    /// <summary>
    /// Повтор: сообщение с таким идентификатором уже приходило. Прежняя квитанция НЕ
    /// переписывается — отправитель, перечитав ее, обязан получить тот же ответ, что и в первый
    /// раз. Файл при этом убирается из «Входящих», иначе он остался бы там навсегда и каждый
    /// запуск спотыкался бы о него заново.
    /// </summary>
    private static void Repeat(InboxLayout layout, InboxFile file, DateTimeOffset now)
    {
        var claimed = InboxFolder.TryClaim(file, Environment.MachineName, now);
        if (claimed is null) return;

        try
        {
            InboxFolder.Finish(layout, claimed, file, accepted: true, now);
        }
        catch (InboxException)
        {
            // Не убрался — вернет уборка брошенных захватов. Данных это не теряет.
        }

        // Своя строка журнала: повтор — единственный случай, когда файл был и работы не
        // случилось, и без строки это выглядело бы как пропажа сообщения.
        RunJournal.Add(RunEntry.New(RunChannel.Inbox, file.Name, layout.Incoming, ok: true,
                                    "повтор: сообщение уже приходило", rows: 0, bytes: 0,
                                    elapsed: TimeSpan.Zero, dbId: 0, at: now, counts: false));
    }

    /// <summary>
    /// Ответить отправителю и убрать файл. Возвращает причину, если ответить не удалось;
    /// <c>null</c> — все сделано.
    ///
    /// Порядок принципиален: сначала квитанция, потом перенос. Упади программа между ними —
    /// файл найдется снова, но квитанция уже лежит, и повтор отсечется по ней. Обратный порядок
    /// оставил бы отправителя без ответа, а сообщение — без памяти о нем.
    /// </summary>
    private static string? Close(Intake taken, bool ok, int rows, string answer, DateTimeOffset now)
    {
        try
        {
            InboxFolder.WriteReceipt(taken.Layout,
                InboxReceipt.OfScenario(taken.File.Name, taken.Id, ok, rows, answer, now));
        }
        catch (InboxException failure)
        {
            // Ответить не вышло (папка «Квитанции» пропала, нет прав). Файл остается
            // захваченным и никуда не едет: перенести его, не сумев ответить, значило бы
            // потерять и файл, и ответ разом. Захват вернется во «Входящие» сам.
            return "ответ отправителю не записался: " + failure.Message;
        }

        try
        {
            InboxFolder.Finish(taken.Layout, taken.Claimed, taken.File, ok, now);
        }
        catch (InboxException failure)
        {
            return "файл не убрался из «Входящих»: " + failure.Message;
        }

        return null;
    }

    /// <summary>Вернуть взятый файл во «Входящие» — сценарий остановил человек, и разбора не было.</summary>
    private static void Abandon(Intake? taken)
    {
        if (taken is { } file) InboxFolder.Release(file.Claimed, file.File);
    }

    /// <summary>
    /// Ключ повтора — ИМЯ ФАЙЛА без расширения: его задает отправитель, и именно его он
    /// повторит, прислав сообщение второй раз при обрыве связи. Имя, негодное в качестве имени
    /// файла квитанции (кириллица, пробелы), ключом не становится — тогда защиты от повтора нет,
    /// и об этом сказано в инструкции: файл, положенный руками, разбирается каждый раз.
    /// </summary>
    private static string MessageId(InboxFile file)
    {
        var stem = Path.GetFileNameWithoutExtension(file.Name);
        return InboxMessage.ValidId(stem) ? stem : "";
    }
}
