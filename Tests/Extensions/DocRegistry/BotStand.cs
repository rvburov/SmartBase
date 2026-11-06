using SmartBase.Extensions;
using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>
/// Прямые тесты правил журнала получают уже подготовленную личность API. Само получение
/// карточки, отсутствие доступа и подмена ФИО проверяются отдельно через PrepareAsync.
/// </summary>
internal static class BotStand
{
    public const string FullName = "Иванов Иван Иванович";

    public static ActionResult Run(ActionContext context, ActionInput input)
    {
        var settings = new Dictionary<string, string>(context.Settings ?? new Dictionary<string, string>());
        var sender = input.Value(RegisterMessageAction.SenderField);
        if (!settings.ContainsKey(DocRegistryExtension.SendersKey)) settings[DocRegistryExtension.SendersKey] = sender;
        return new RegisterMessageAction().Run(context with { Settings = settings },
            input with { Identity = input.Identity ?? new ActionIdentity(sender, FullName) });
    }
}
