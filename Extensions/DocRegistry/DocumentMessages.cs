using System.Runtime.CompilerServices;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>
/// Просмотр и правка журнала из переписки. Ожидание подтверждения принадлежит открытому
/// репозиторию, базе, коллекции и отправителю. После закрытия базы или перезапуска оно исчезает.
/// Данные не меняются до «Да»; подтверждается именно показанный человеку снимок.
/// </summary>
internal static class DocumentMessages
{
    private const string View = "Просмотр", Delete = "Удаление", Edit = "Изменение";
    private const string Choices = "Регистрация, Просмотр, Удаление, Изменение";
    private const string AnswerPrompt = "Подтвердите: Да или Нет";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private static readonly ConditionalWeakTable<object, Waiting> WaitingByRepo = new();

    private sealed class Waiting
    {
        public Dictionary<(int Db, int Collection, string Channel, string Sender), Pending> Items { get; } = [];
    }

    private sealed record Card(int Id, string Number, string Name, string Fio, string Date,
                               string Project, string Accounting, string FirstUse, string Summary,
                               string? Version)
    {
        public string Text => string.Join("\n",
            $"Номер: {Number}", $"Наименование: {Name}", $"Проект: {Project}", $"ФИО: {Fio}",
            $"Дата регистрации: {Show(Date)}", $"Отметка учета: {Show(Accounting)}",
            $"Примечание (первичное применение): {Show(FirstUse)}",
            $"Примечание (краткая характеристика): {Show(Summary)}");
        private static string Show(string value) => value.Length == 0 ? "—" : value;
    }

    private sealed record Pending(string Action, Card Before, RegistryShape Shape,
                                  IReadOnlyDictionary<string, string> Changes, DateTimeOffset Expires);

    /// <summary>Null — обычная регистрация: ее продолжает существующее действие.</summary>
    public static ActionResult? Run(ActionContext context, string text, string sender)
    {
        var lines = MessageRegistration.Lines(text);
        var action = MessageRegistration.Value(lines, MessageRegistration.ActionKey);
        var plain = MessageRegistration.Plain(text).Trim();
        var yes = RegistrySchema.SameName(plain, "Да");
        var no = RegistrySchema.SameName(plain, "Нет");
        var known = new[] { View, Delete, Edit }.FirstOrDefault(a => RegistrySchema.SameName(a, action));
        var registration = RegistrySchema.SameName(action, MessageRegistration.RegisterWord);

        if (action.Length > 0 && !registration && known is null)
            return ActionResult.Refused($"Действие: неизвестное действие (Можно: {Choices})");

        if (context.Repo is not { } repo)
            return known is not null || yes || no
                ? ActionResult.Failed("действию не дали подключения к базе данных") : null;

        var waiting = WaitingByRepo.GetValue(context.Session ?? repo, _ => new Waiting());
        var key = (context.DbId, context.CollectionId, context.Channel, sender.ToUpperInvariant());
        lock (waiting)
        {
            if (waiting.Items.TryGetValue(key, out var pending) && pending.Expires <= context.Now)
            {
                waiting.Items.Remove(key);
                if (yes || no)
                    return ActionResult.Refused("Подтверждение: срок истек. Отправьте запрос заново");
                pending = null;
            }

            if (yes || no)
            {
                if (pending is null)
                    return ActionResult.Refused("Подтверждение: нет операции, ожидающей ответа. Отправьте запрос заново");
                // Забираем один раз, в том числе при отказе в правах или сбое записи.
                waiting.Items.Remove(key);
                if (no) return Reply("Операция отменена", 0);
                return Confirm(context, repo, pending);
            }

            if (pending is not null && known != View)
                return ActionResult.Refused($"Ожидается подтверждение: {pending.Action}, {pending.Before.Number}. "
                                          + $"{AnswerPrompt}. Для нового запроса сначала ответьте Нет");

            if (known is null) return null;

            var problems = new List<string>();
            var numberText = MessageRegistration.Value(lines, MessageRegistration.NumberKey);
            var number = ExactNumber(numberText);
            if (numberText.Length == 0) problems.Add("Номер: не указан номер");
            else if (number is null)
                problems.Add("Номер: нужен полный номер документа или исполнения (Пример: АБВГ.301121.001 или АБВГ.301121.001-01)");

            var changes = new Dictionary<string, string>();
            if (known == Edit)
            {
                foreach (var (field, value) in lines)
                {
                    if (RegistrySchema.SameName(field, "Действие") || RegistrySchema.SameName(field, "Номер")) continue;
                    var editable = new[] { "Наименование", "Проект", "ФИО" }
                        .FirstOrDefault(f => RegistrySchema.SameName(f, field));
                    if (editable is null) problems.Add($"{field}: изменение этого поля не поддерживается");
                    else if (value.Length == 0) problems.Add($"{editable}: не указано значение");
                    else if (editable == "ФИО" && !RegistryImport.IsFullName(value))
                        problems.Add($"ФИО: не полное ФИО (Пример: {MessageRegistration.FioExample})");
                    else changes[editable] = value;
                }
                if (changes.Count == 0 && problems.Count == 0)
                    problems.Add("Изменение: укажите хотя бы одно поле — Наименование, Проект или ФИО");
            }
            else
            {
                foreach (var (field, _) in lines)
                    if (!RegistrySchema.SameName(field, "Действие") && !RegistrySchema.SameName(field, "Номер"))
                        problems.Add($"{field}: для действия «{known}» нужны только Действие и Номер");
            }
            if (problems.Count > 0) return ActionResult.Refused(string.Join("\n", problems));

            try
            {
                Demand(repo, context.CollectionId, known);
                var check = Shape(repo, context.CollectionId);
                if (check.Shape is not { } shape) return ActionResult.Failed(check.Problem!);
                var (card, problem) = Find(repo, shape, number!, context);
                if (card is null) return ActionResult.Refused(problem);
                if (known == View) return Reply(card.Text, 1);

                var after = Apply(card, changes);
                if (known == Edit && after == card)
                    return ActionResult.Refused("Изменение: новые значения совпадают с текущими. Изменений нет");

                foreach (var expired in waiting.Items.Where(p => p.Value.Expires <= context.Now).Select(p => p.Key).ToArray())
                    waiting.Items.Remove(expired);
                if (waiting.Items.Count >= 256)
                    return ActionResult.Refused("Подтверждение: слишком много ожидающих операций. Повторите запрос позже");
                waiting.Items[key] = new Pending(known, card, shape, changes, context.Now + Lifetime);
                var preview = known == Delete ? $"Подтвердите удаление документа:\n{card.Text}"
                    : $"Подтвердите изменение документа:\n{card.Text}\n\nИзменения:\n"
                      + string.Join("\n", changes.Select(c => $"{c.Key}: {Value(card, c.Key)} → {c.Value}"));
                return Reply($"{preview}\n\n{AnswerPrompt}\nПодтверждение действует 10 минут", 0);
            }
            catch (PermissionDeniedException error) { return ActionResult.Refused(error.Message); }
        }
    }

    private static ActionResult Confirm(ActionContext context, ConstructorRepository repo, Pending pending)
    {
        try
        {
            Demand(repo, context.CollectionId, pending.Action);
            return repo.InWriteTransaction(() =>
            {
                var check = Shape(repo, context.CollectionId);
                if (check.Shape is not { } shape) return ActionResult.Failed(check.Problem!);
                var (current, problem) = Find(repo, shape, pending.Before.Number, context);
                if (current is null) return ActionResult.Refused(problem);
                if (shape != pending.Shape || current != pending.Before)
                    return ActionResult.Refused("Документ изменился после запроса подтверждения. Операция не выполнена. Отправьте запрос заново");

                if (pending.Action == Delete)
                {
                    repo.DeleteRecord(current.Id);
                    return Reply($"Удалено\n{current.Text}", 1);
                }
                foreach (var (field, value) in pending.Changes)
                {
                    if (field == "Наименование") repo.SetRecordTitle(current.Id, value);
                    else repo.SetFieldValue(current.Id, field == "Проект" ? shape.ProjectFieldId : shape.FioFieldId, value);
                }
                return Reply($"Изменено\n{Apply(current, pending.Changes).Text}", 1);
            });
        }
        catch (PermissionDeniedException error) { return ActionResult.Refused(error.Message); }
    }

    private static void Demand(ConstructorRepository repo, int collection, string action)
    {
        repo.Permissions?.Demand(PermAction.ViewCollection, collection);
        if (action != View)
            repo.Permissions?.Demand(action == Delete ? PermAction.DeleteRecord : PermAction.EditRecord, collection);
    }

    private static SchemaCheck Shape(ConstructorRepository repo, int collectionId)
    {
        var collection = repo.GetAllCollections().FirstOrDefault(c => c.Id == collectionId);
        return collection is null ? new SchemaCheck(null, "коллекция журнала не найдена в этой базе данных")
            : RegistrySchema.Check(repo, collectionId, collection.Name);
    }

    private static (Card? Card, string Problem) Find(ConstructorRepository repo, RegistryShape shape, string number,
                                                  ActionContext context)
    {
        // Точный отбор по одному столбцу: не выгружаем весь журнал ради одного номера.
        // Организация хранится в написании из настроек (включая похожие латинские буквы).
        var parts = number.Split('.');
        var spellings = new HashSet<string>(StringComparer.Ordinal) { number, number.ToUpperInvariant(), number.ToLowerInvariant() };
        foreach (var org in DocRegistryExtension.ParseOrgCodes(context.Setting(DocRegistryExtension.OrgCodesKey)))
            if (MessageRegistration.Org(org) == MessageRegistration.Org(parts[0]))
                spellings.Add($"{org}.{parts[1]}.{parts[2]}");
        var ids = repo.GetFilteredRecordIds(shape.CollectionId,
            new Dictionary<string, HashSet<string>> { [shape.DesignationFieldId.ToString()] = spellings });
        if (ids.Count == 0) return (null, $"Номер: документ {number} не найден");
        if (ids.Count > 1) return (null, $"Номер: в журнале несколько записей {number}. Обратитесь к администратору");
        var record = repo.GetRecord(ids[0], refresh: true);
        if (record is null || record.CollectionId != shape.CollectionId)
            return (null, $"Номер: документ {number} не найден");
        var fields = repo.GetFieldValuesBatch(ids).GetValueOrDefault(ids[0]) ?? [];
        string Field(int id) => fields.GetValueOrDefault(id, "");
        return (new Card(ids[0], Field(shape.DesignationFieldId), record.Title, Field(shape.FioFieldId),
            Field(shape.DateFieldId), Field(shape.ProjectFieldId), Field(shape.AccountingFieldId),
            Field(shape.FirstUseFieldId), Field(shape.SummaryFieldId), record.UpdatedAt), "");
    }

    private static Card Apply(Card card, IReadOnlyDictionary<string, string> changes) => card with
    {
        Name = changes.GetValueOrDefault("Наименование", card.Name),
        Project = changes.GetValueOrDefault("Проект", card.Project),
        Fio = changes.GetValueOrDefault("ФИО", card.Fio),
    };

    private static string Value(Card card, string field) => field switch
    {
        "Наименование" => card.Name, "Проект" => card.Project, _ => card.Fio,
    };

    private static ActionResult Reply(string text, int rows) => new(true, text.Split('\n')[0], rows, Reply: text);

    private static string? ExactNumber(string text)
    {
        var parts = string.Concat(text.Where(c => !char.IsWhiteSpace(c))).Split('.');
        if (parts.Length != 3) return null;
        var org = MessageRegistration.Org(parts[0]);
        if (org.Length != 4 || !org.All(c => c is >= 'А' and <= 'Я') || !EskdCatalog.IsFullCode(parts[1])) return null;
        var tail = parts[2].Split('-');
        if (tail.Length is < 1 or > 2 || tail[0].Length != 3 || !tail[0].All(char.IsAsciiDigit)
            || !int.TryParse(tail[0], out var number) || number is < 1 or > 999) return null;
        if (tail.Length == 1) return Designation.Format(org, parts[1], number);
        return tail[1].Length == 2 && tail[1].All(char.IsAsciiDigit) && int.TryParse(tail[1], out var execution)
            && execution is >= 1 and <= 99 ? Designation.FormatExecution(org, parts[1], number, execution) : null;
    }
}
