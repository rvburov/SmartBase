using SmartBase.Core.Models;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>
/// Действие «Зарегистрировать по сообщению»: разбор сообщения, проверка, выдача номера и ответ
/// отправителю — одним шагом.
///
/// Рядом уже есть пара «разобрать сообщение» + «зарегистрировать документ», и она остается для
/// папки обмена. Переписке она не годится по двум причинам, и обе — про ответ человеку:
///
/// <list type="number">
///   <item><b>Ошибки все сразу и словами отправителя.</b> Разбор общего вида не знает, какие
///   строки обязательны: пропущенная строка «Проект» доезжает до регистрации пропущенной
///   колонкой, и сценарий останавливается фразой для того, кто его настраивал, а не «Проект: не
///   указан проект». Здесь сообщение проверяется целиком (<see cref="MessageRegistration"/>).</item>
///   <item><b>Номер по маске.</b> <c>ХХХ</c> и <c>001-ХХ</c> — это просьба подобрать номер или
///   исполнение, и выполнить ее можно только с журналом в руках.</item>
/// </list>
///
/// <b>Откуда пришло сообщение, действие не знает.</b> Текст и отправителя приносит шаг сценария:
/// какая колонка события ляжет в какое поле, решает человек при настройке шага. Поэтому одно и
/// то же действие работает с любой программой, которая умеет прислать текст и назвать того, кто
/// его написал.
///
/// <b>Регистрирует только тот, кто в списке.</b> Список ведется в настройках расширения, и
/// сверяется с ним ОТПРАВИТЕЛЬ — идентификатор, который назвала та программа, — а не строка «ФИО»
/// сообщения: ее можно написать любую. Пустой список — не «можно всем», а «никому»:
/// регистрацию по переписке включают осознанно.
///
/// <b>Два вида отказа.</b> Причину в самом сообщении отправитель исправит сам, и отказ обращен к
/// нему (<see cref="ActionResult.Refused"/>). Причина в настройке — журнал не тот, подключения
/// нет — отправителю ничего не скажет; она возвращается обычным отказом, и что на этот случай
/// ответить отправителю, решает сценарий.
///
/// Логика регистрации не дублируется: номер выдает тот же <see cref="DocRegistrar"/>, что и окно,
/// и подбирается он прямо перед записью.
/// </summary>
public sealed class RegisterMessageAction : IExtensionAction
{
    /// <summary>Ключ действия внутри расширения.</summary>
    public const string ActionKey = "register-message";

    /// <summary>Поле: текст сообщения.</summary>
    public const string TextField = "text";

    /// <summary>Поле: идентификатор отправителя — как его называет программа, приславшая текст.</summary>
    public const string SenderField = "sender";

    /// <summary>Первая строка ответа об удаче.</summary>
    public const string RegisteredWord = "Зарегистрировано";

    /// <summary>Отказ тому, кого нет в списке. Без подробностей: чужому они ни к чему.</summary>
    public const string NotListed = "Вы не в списке регистрирующих. Обратитесь к администратору";

    public string Key         => ActionKey;
    public string Name        => "Зарегистрировать по сообщению";
    public string Description =>
        "Обрабатывает действия Регистрация, Просмотр, Удаление и Изменение. При регистрации "
      + "выдает номер или исполнение по ГОСТ 2.201-80. Удаление и изменение выполняет после "
      + "ответа Да, ответ Нет отменяет операцию. Доступ — по настройке «Кто регистрирует по сообщениям».";

    public ExtensionActionKind Kind => ExtensionActionKind.Operation;
    public bool ChecksPermissions => true;

    public IReadOnlyList<ExtensionActionField> Fields =>
    [
        // Текст необязателен намеренно: на пустое сообщение отправитель должен получить перечень
        // того, чего в нем не хватает, а не молчание.
        new(TextField,   "Текст сообщения", Required: false, Watermark: "{{колонка с текстом}}"),
        new(SenderField, "Отправитель",     Watermark: "{{колонка с идентификатором отправителя}}"),
    ];

    public ActionResult Run(ActionContext context, ActionInput input)
    {
        // Допуск — первым и отдельно: тому, кого нет в списке, разбор его сообщения не нужен.
        var senders = MessageRegistration.Senders(context.Setting(DocRegistryExtension.SendersKey));
        var sender  = input.Value(SenderField);

        if (sender.Length == 0)
            return ActionResult.Failed(
                "отправитель не назван: в шаге не заполнено поле «Отправитель» либо в событии "
                + "нет такой колонки");

        // Поле не заполнено В ШАГЕ — это настройка, а не сообщение: отправитель написал все как
        // надо, а текст до действия не доехал. Перечень «не указано…» в ответ на правильное
        // сообщение отправил бы его искать ошибку, которой у него нет.
        if (!input.Values.ContainsKey(TextField))
            return ActionResult.Failed(
                "текст сообщения действию не передан: в шаге не заполнено поле «Текст сообщения» — "
                + "в нем называют колонку события с текстом, в двойных фигурных скобках");

        // Объяснение и ответ здесь разные: отправителю — что его нет в списке, настраивавшему —
        // что список пуст, иначе он искал бы этого отправителя в списке, которого нет.
        if (senders.Count == 0)
            return new ActionResult(false,
                "список регистрирующих по сообщениям пуст: заполните настройку расширения "
                + $"«{DocRegistryExtension.SendersLabel}»", Reply: NotListed);

        if (!senders.Contains(sender))
            return new ActionResult(false, $"отправителя «{sender}» нет в списке регистрирующих",
                                    Reply: NotListed);

        var orgs = DocRegistryExtension.ParseOrgCodes(context.Setting(DocRegistryExtension.OrgCodesKey));

        var text = input.Value(TextField);

        if (DocumentMessages.Run(context, text, sender) is { } documentResult)
            return documentResult;

        var (request, problems) = MessageRegistration.Read(text, orgs);

        if (request is null)
        {
            var reply = string.Join("\n", problems);

            // Не узнано ни одной строки — отправителю перечень тот же, а тому, кто настраивал
            // обмен, надо знать, что действие получило: пустую колонку или текст не того вида.
            // Сам текст в объяснение не идет: оно ложится в журнал.
            return MessageRegistration.KnownLines(text) > 0
                ? ActionResult.Refused(reply)
                : new ActionResult(false, reply + "\n" + NothingRead(text), Reply: reply);
        }

        if (context.Repo is not { } repo)
            return ActionResult.Failed("действию не дали подключения к базе данных");

        var collection = repo.GetAllCollections().FirstOrDefault(c => c.Id == context.CollectionId);

        if (collection is null)
            return ActionResult.Failed("коллекция журнала не найдена в этой базе данных");

        // Структура журнала сверяется КАЖДЫЙ раз — тем же правилом, что у регистрации шагом:
        // столбец могли переставить вчера, и значения легли бы не туда.
        var check = RegistrySchema.Check(repo, context.CollectionId, collection.Name);

        if (!check.Ok || check.Shape is not { } shape)
            return ActionResult.Failed(
                $"коллекция «{collection.Name}» не подходит под журнал регистрации: {check.Problem}");

        int? execution = null;

        if (request.Number is { } number)
        {
            // Самого документа в журнале может не быть — правило то же, что у загрузки из файла:
            // старые документы туда часто не заносились, и запрет означал бы, что их исполнения
            // внести нельзя. Исполнение выдается первое свободное у этого номера.
            execution = Designation.FirstFreeExecution(DocRegistrar.Designations(repo, shape),
                                                       request.Org, request.Eskd, number);

            if (execution is null)
                return ActionResult.Refused(
                    $"{MessageRegistration.NumberKey}: у документа "
                    + $"{Designation.Format(request.Org, request.Eskd, number)} заняты все исполнения "
                    + $"{Designation.MinExecution:D2}–{Designation.MaxExecution:D2}");
        }

        try
        {
            repo.Permissions?.Demand(PermAction.CreateRecord, context.CollectionId);
            repo.Permissions?.Demand(PermAction.EditRecord, context.CollectionId);
            var done = repo.InWriteTransaction(() => DocRegistrar.Register(repo, shape,
                new RegistrationInput(
                    Name:      request.Name,
                    Org:       request.Org,
                    Eskd:      request.Eskd,
                    Fio:       request.Fio,
                    Project:   request.Project,
                    Execution: execution,
                    Number:    request.Number),
                context.Now.LocalDateTime));

            // В журнал — одной строкой, отправителю — строками: ответ читается тем же взглядом,
            // что и сообщение, и сверяется с ним построчно.
            return new ActionResult(true,
                $"{RegisteredWord} {done.Designation} {request.Project} {request.Fio}",
                Rows: 1, Reply: Registered(done.Designation, request));
        }
        catch (DocRegistryException failure)
        {
            // Исчерпанная серия, занятое за это мгновение исполнение — причина в номере, и
            // отправитель может назвать другой.
            return ActionResult.Refused($"{MessageRegistration.NumberKey}: {failure.Message}");
        }
        catch (PermissionDeniedException failure) { return ActionResult.Refused(failure.Message); }
    }

    /// <summary>
    /// Ответ об удаче: что зарегистрировано — теми же строками и в том же порядке, что в
    /// сообщении. На месте метки стоит выданный номер, остальное — как записано в журнал.
    /// </summary>
    private static string Registered(string designation, RegistrationRequest request) =>
        string.Join("\n",
            RegisteredWord,
            $"{MessageRegistration.NumberKey}: {designation}",
            $"{MessageRegistration.NameKey}: {request.Name}",
            $"{MessageRegistration.ProjectKey}: {request.Project}",
            $"{MessageRegistration.FioKey}: {request.Fio}");

    /// <summary>Пометка для журнала: что действие получило вместо сообщения.</summary>
    private static string NothingRead(string text) => text.Length == 0
        ? "(текст сообщения пришел пустым: проверьте колонку, названную в поле «Текст сообщения»)"
        : $"(в полученном тексте — знаков: {text.Length} — нет ни одной строки сообщения: "
          + "проверьте, что названо в поле «Текст сообщения»)";
}
