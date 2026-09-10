using System.Globalization;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>
/// Действие «Зарегистрировать документ» — та же регистрация, что и в окне, но без окна: шагом
/// сценария (docs/ROADMAP-API.md, раздел 9).
///
/// <b>Логика регистрации не дублируется.</b> Действие — тонкая прослойка над
/// <see cref="DocRegistrar"/>: разобрать значения, проверить коллекцию, позвать то же самое.
/// Если бы ради обмена регистрацию пришлось повторить рядом, граница была бы проведена неверно
/// — и правило ГОСТ, поправленное в одном месте, разошлось бы со вторым.
///
/// Три отличия от ручного ввода, и все три решены здесь.
///
/// <list type="number">
///   <item><b>Гонка номеров.</b> Номер подбирается прямо перед записью, внутри
///   <see cref="DocRegistrar.Register"/>, — два сообщения в одну секунду не получат один
///   номер.</item>
///   <item><b>Подтверждать некому.</b> В окне человек соглашается писать поверх занятого
///   исполнения (<c>ConfirmedDuplicate</c>); здесь собеседника нет, и занятое исполнение —
///   отказ с объяснением, никогда молчаливая перезапись.</item>
///   <item><b>Причина едет словами.</b> Неудача возвращается результатом, а не исключением: ее
///   ждут в квитанции отправителю, а не в журнале разработчика.</item>
/// </list>
///
/// Коллекцию действие не выбирает — ее задает шаг сценария: одно расширение можно повесить на
/// несколько журналов и каналов.
/// </summary>
public sealed class RegisterAction : IExtensionAction
{
    /// <summary>Ключ действия внутри расширения.</summary>
    public const string ActionKey = "register";

    public string Key         => ActionKey;
    public string Name        => "Зарегистрировать документ";
    public string Description => "Присваивает обозначение по ГОСТ 2.201-80 и заводит строку в "
                               + "журнале регистрации — той коллекции, что выбрана в шаге.";

    public ExtensionActionKind Kind => ExtensionActionKind.Operation;

    public IReadOnlyList<ExtensionActionField> Fields =>
    [
        new("name",       "Наименование ЕСКД",                    Watermark: "Кронштейн"),
        new("org",        "Код организации",                      Watermark: "АААА"),
        new("eskd",       "Код ЕСКД",                             Watermark: "000000"),
        new("project",    "Проект",                               Watermark: "Молоток"),
        new("fio",        "ФИО регистрирующего",   Required: false),

        // Номер и исполнение — пара. Пустой номер означает «подобрать первый свободный», и это
        // обычный случай; заполненный нужен ровно затем, чтобы завести исполнение УЖЕ выданного
        // документа, — иначе исполнение уехало бы к следующему номеру серии и означало бы совсем
        // другой документ.
        new("number",     "Номер документа",       Required: false, Watermark: "005"),
        new("execution",  "Исполнение",            Required: false, Watermark: "1"),
        new("accounting", "Отметка учета",         Required: false),
        new("firstUse",   "Примечание (первичное применение)", Required: false),
        new("summary",    "Примечание (краткая характеристика)", Required: false),
    ];

    public ActionResult Run(ActionContext context, ActionInput input)
    {
        // Операции подключение дает оболочка, и оно заполнено всегда. Проверка здесь — не
        // недоверие, а вежливость: отказ словами понятнее, чем падение в фоне.
        if (context.Repo is not { } repo)
            return ActionResult.Failed("действию не дали подключения к базе данных");

        var collection = repo.GetAllCollections().FirstOrDefault(c => c.Id == context.CollectionId);

        if (collection is null)
            return ActionResult.Failed("коллекция журнала не найдена в этой базе данных");

        // Структура коллекции сверяется КАЖДЫЙ раз, а не при настройке шага: столбец могли
        // переименовать или переставить вчера, и тогда значения легли бы не туда.
        var check = RegistrySchema.Check(repo, context.CollectionId, collection.Name);

        if (!check.Ok || check.Shape is not { } shape)
            return ActionResult.Failed(
                $"коллекция «{collection.Name}» не подходит под журнал регистрации: {check.Problem}");

        if (!Number(input, "number", out var number, out var problem)) return ActionResult.Failed(problem);
        if (!Number(input, "execution", out var execution, out problem)) return ActionResult.Failed(problem);

        var registration = new RegistrationInput(
            Name:       input.Value("name"),
            Org:        input.Value("org"),
            Eskd:       input.Value("eskd"),
            Fio:        input.Value("fio"),
            Project:    input.Value("project"),
            Accounting: input.Value("accounting"),
            FirstUse:   input.Value("firstUse"),
            Summary:    input.Value("summary"),
            Execution:  execution,
            Number:     number);

        try
        {
            var done = DocRegistrar.Register(repo, shape, registration,
                                             context.Now.LocalDateTime);

            return ActionResult.Done("зарегистрировано: " + done.Designation);
        }
        catch (DocRegistryException failure)
        {
            // Занятое исполнение, исчерпанная серия, незаполненное обязательное поле — все это
            // ответ отправителю, а не поломка программы.
            return ActionResult.Failed(failure.Message);
        }
    }

    /// <summary>
    /// Необязательное числовое поле. Пусто — <c>null</c> (расширение решит само); мусор —
    /// отказ с названным полем: «не число» без имени поля отправителю ничего не говорит.
    /// </summary>
    private static bool Number(ActionInput input, string key, out int? value, out string problem)
    {
        value   = null;
        problem = "";

        var raw = input.Value(key);
        if (raw.Length == 0) return true;

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 0)
        {
            problem = $"поле «{key}»: «{raw}» — не число";
            return false;
        }

        value = parsed;
        return true;
    }
}
