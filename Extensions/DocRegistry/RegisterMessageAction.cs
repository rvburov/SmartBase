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

    public string Key         => ActionKey;
    public string Name        => "Зарегистрировать по сообщению";
    public string Description =>
        "Обрабатывает действия Регистрация, Просмотр, Удаление и Изменение. При регистрации "
      + "выдает номер или исполнение по ГОСТ 2.201-80. Первый документ новой серии, удаление и "
      + "изменение выполняет после ответа Да, ответ Нет отменяет операцию.";

    public ExtensionActionKind Kind => ExtensionActionKind.Operation;

    public IReadOnlyList<ExtensionActionField> Fields =>
    [
        // Текст необязателен намеренно: на пустое сообщение отправитель должен получить перечень
        // того, чего в нем не хватает, а не молчание.
        new(TextField,   "Текст сообщения", Required: false, Watermark: "{{колонка с текстом}}"),
        new(SenderField, "Отправитель",     Watermark: "{{колонка с идентификатором отправителя}}"),
    ];

    public ActionResult Run(ActionContext context, ActionInput input)
    {
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

        var orgs = DocRegistryExtension.ParseOrgCodes(context.Setting(DocRegistryExtension.OrgCodesKey));

        var text = input.Value(TextField);

        if (RegistrySchema.SameName(MessageRegistration.Plain(text).Trim(), "Инструкция"))
            return new ActionResult(true, "Выдана инструкция по работе с журналом КД",
                                    Rows: 0, Reply: MessageInstructions.Text);

        if (DocumentMessages.Run(context, text, sender) is { } documentResult)
            return documentResult;

        var (request, problems) = MessageRegistration.Read(text, orgs);

        if (request is null)
        {
            var plain = string.Join("\n", problems);

            // Не узнано ни одной строки — отправителю перечень тот же, а тому, кто настраивал
            // обмен, надо знать, что действие получило: пустую колонку или текст не того вида.
            // Сам текст в объяснение не идет: оно ложится в журнал.
            return MessageRegistration.KnownLines(text) > 0
                ? ReplyMarkup.Refused(plain)
                : new ActionResult(false, plain + "\n" + NothingRead(text),
                                   Reply: ReplyMarkup.Problems(plain));
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

        return DocumentMessages.Register(context, request, sender, shape);
    }

    /// <summary>
    /// Ответ об удаче: что зарегистрировано — теми же строками и в том же порядке, что в
    /// сообщении. На месте метки стоит выданный номер, остальное — как записано в журнал.
    /// Заголовок и номер — жирным (<see cref="ReplyMarkup"/>).
    /// </summary>
    internal static string Registered(string designation, RegistrationRequest request,
                                      string heading = RegisteredWord) =>
        string.Join("\n",
            ReplyMarkup.Bold(ReplyMarkup.Escape(heading)),
            $"{MessageRegistration.NumberKey}: {ReplyMarkup.Bold(ReplyMarkup.Escape(designation))}",
            $"{MessageRegistration.NameKey}: {ReplyMarkup.Escape(request.Name)}",
            $"{MessageRegistration.ProjectKey}: {ReplyMarkup.Escape(request.Project)}",
            $"{MessageRegistration.FioKey}: {ReplyMarkup.Escape(request.Fio)}");

    /// <summary>Пометка для журнала: что действие получило вместо сообщения.</summary>
    private static string NothingRead(string text) => text.Length == 0
        ? "(текст сообщения пришел пустым: проверьте колонку, названную в поле «Текст сообщения»)"
        : $"(в полученном тексте — знаков: {text.Length} — нет ни одной строки сообщения: "
          + "проверьте, что названо в поле «Текст сообщения»)";
}
