using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>Несколько номеров в одном сообщении — один подбор, подтверждение и коммит.</summary>
internal static partial class DocumentMessages
{
    private sealed record SeriesSnapshot(string Org, string Eskd, string[] Values);
    private sealed record BatchPlan(RegistrationRequest Common, ImportRow[] Rows, SeriesSnapshot[] Series);

    public static ActionResult RegisterBatch(ActionContext context, BatchRegistrationRead batch, string sender,
                                             RegistryShape expectedShape)
    {
        var repo = context.Repo!;
        var waiting = WaitingByRepo.GetValue(context.Session ?? repo, _ => new Waiting());
        var key = (context.DbId, context.CollectionId, context.Channel, sender.ToUpperInvariant());
        lock (waiting)
        {
            if (waiting.Items.TryGetValue(key, out var active) && active.Expires > context.Now)
                return ReplyMarkup.RefusedHeaded("Ожидается подтверждение. Для нового запроса сначала ответьте Нет");
            try
            {
                // Замок коллекции берется ДО чтения номеров, как в окне и импорте. Отдельная
                // транзакция на каждый элемент позволила бы записать лишь часть заявки.
                return repo.InCollectionWriteTransaction(expectedShape.CollectionId, () =>
                {
                    var check = Shape(repo, context.CollectionId);
                    if (check.Shape is not { } shape) return ActionResult.Failed(check.Problem!);
                    var journal = DocRegistrar.Designations(repo, shape);
                    var snapshots = batch.Items.Where(i => i.Request is not null)
                        .Select(i => (i.Request!.Org, i.Request.Eskd)).Distinct()
                        .Select(s => new SeriesSnapshot(s.Org, s.Eskd, InSeries(journal, s.Org, s.Eskd))).ToArray();
                    var occupied = snapshots.ToDictionary(s => (s.Org, s.Eskd), s => s.Values.ToList());
                    var rows = new List<ImportRow>();
                    var statuses = new List<string>();
                    var hasErrors = !batch.Ok;
                    foreach (var item in batch.Items)
                    {
                        var prefix = $"{item.Position}. {(item.Raw.Length == 0 ? "(пустой номер)" : item.Raw)} — ";
                        if (item.Request is not { } request || item.Problems.Count > 0)
                        {
                            hasErrors = true;
                            statuses.Add(prefix + string.Join("; ", item.Problems));
                            continue;
                        }
                        var values = occupied[(request.Org, request.Eskd)];
                        var (designation, problem) = Allocate(request, values);
                        if (designation is null)
                        {
                            hasErrors = true;
                            statuses.Add(prefix + problem);
                            continue;
                        }
                        // Повтор маски намеренный: следующий элемент видит уже подобранное
                        // обозначение. В журнале до полного успеха все еще нет новых строк.
                        values.Add(designation);
                        rows.Add(new ImportRow(item.Position, request.Name, designation, request.Fio,
                            request.Project, "", "", ""));
                        var snapshot = snapshots.First(s => s.Org == request.Org && s.Eskd == request.Eskd);
                        statuses.Add(prefix + (snapshot.Values.Length > 0
                            ? "проверен. Серия уже существует."
                            : EskdCatalog.Find(request.Eskd) is null
                                ? "код классификации не найден в доступном справочнике ЕСКД. Для новой серии потребуется подтверждение."
                                : "проверен. Код ЕСКД найден в справочнике. Для новой серии потребуется подтверждение."));
                    }
                    if (hasErrors)
                        return RefuseBatch(batch.Count, [.. batch.Problems, .. statuses,
                            "Исправьте ошибки и отправьте весь список заново."]);

                    var plan = new BatchPlan(batch.Items[0].Request!, rows.ToArray(), snapshots);
                    var newSeries = snapshots.Where(s => s.Values.Length == 0).ToArray();
                    if (newSeries.Length == 0) return WriteBatch(context, repo, shape, plan);

                    foreach (var expired in waiting.Items.Where(p => p.Value.Expires <= context.Now).Select(p => p.Key).ToArray())
                        waiting.Items.Remove(expired);
                    if (waiting.Items.Count >= 256)
                        return RefuseBatch(plan.Rows.Length, ["Подтверждение: слишком много ожидающих операций. Повторите запрос позже"]);
                    var card = new Card(0, string.Join(", ", plan.Rows.Select(r => r.Designation)), plan.Common.Name,
                        plan.Common.Fio, "", plan.Common.Project, "", "", "", null);
                    waiting.Items[key] = new Pending(MessageRegistration.RegisterWord, card, shape,
                        new Dictionary<string, string>(), context.Now + Lifetime, Batch: plan);
                    var warnings = string.Join("\n\n", newSeries.Select(FirstSeriesWarning));
                    return Reply($"{BatchCard(plan, $"Заявка на регистрацию: {plan.Rows.Length}")}\n\n{warnings}\n\n"
                        + $"{ReplyMarkup.Bold("Подтверждение относится ко всему списку. До ответа Да ничего не зарегистрировано.")}\n{Prompt}", 0);
                });
            }
            catch (DocRegistryException error)
            {
                return RefuseBatch(batch.Count, [$"Номер: {error.Message}", "Отправьте весь список заново."]);
            }
        }
    }

    private static string[] InSeries(IEnumerable<string> journal, string org, string eskd) =>
        journal.Where(d => Designation.NumberIn(d, org, eskd) is not null)
            .OrderBy(d => d, StringComparer.Ordinal).ToArray();

    private static (string? Designation, string Problem) Allocate(RegistrationRequest request, List<string> occupied)
    {
        var number = request.Number ?? Designation.FirstFree(occupied, request.Org, request.Eskd);
        if (number is null) return (null, "Номер: в серии заняты все номера 001–999");
        if (request.NewExecution)
        {
            var execution = Designation.FirstFreeExecution(occupied, request.Org, request.Eskd, number.Value);
            return execution is { } e
                ? (Designation.FormatExecution(request.Org, request.Eskd, number.Value, e), "")
                : (null, "Номер: у документа заняты все исполнения 01–99");
        }
        if (occupied.Any(d => Designation.NumberIn(d, request.Org, request.Eskd) == number))
            return (null, $"Номер: документ {Designation.Format(request.Org, request.Eskd, number.Value)} уже зарегистрирован");
        return (Designation.Format(request.Org, request.Eskd, number.Value), "");
    }

    private static string FirstSeriesWarning(SeriesSnapshot series)
    {
        var label = ReplyMarkup.Bold(ReplyMarkup.Escape($"Серия {series.Org}.{series.Eskd}:"));
        if (EskdCatalog.Find(series.Eskd) is null)
            return label + "\n" + ReplyMarkup.Bold("Код классификации не найден в доступном справочнике ЕСКД. Возможно, он относится к закрытому классификатору. Вы уверены, что номер указан верно?");
        return label + "\n" + string.Join("\n", EskdCatalog.Chain(series.Eskd).Select(n =>
            $"{n.Code} — {ReplyMarkup.Escape(n.Name.Length == 0 ? "наименование отсутствует в справочнике" : n.Name)}"))
            + "\n" + ReplyMarkup.Bold("Это первый номер в данной серии классификатора. Вы уверены, что номер верный?");
    }

    private static ActionResult ConfirmBatch(ActionContext context, ConstructorRepository repo,
                                              RegistryShape expectedShape, BatchPlan plan) =>
        repo.InCollectionWriteTransaction(expectedShape.CollectionId, () =>
        {
            var check = Shape(repo, context.CollectionId);
            if (check.Shape is not { } shape)
                return RefuseBatch(plan.Rows.Length, ["Структура журнала изменилась. Отправьте запрос заново."]);
            var orgs = DocRegistryExtension.ParseOrgCodes(context.Setting(DocRegistryExtension.OrgCodesKey));
            if (orgs.Count > 0 && plan.Series.Any(s => !orgs.Any(o => MessageRegistration.Org(o) == MessageRegistration.Org(s.Org))))
                return RefuseBatch(plan.Rows.Length, ["Номер: код организации больше не разрешен. Отправьте запрос заново."]);
            var journal = DocRegistrar.Designations(repo, shape);
            if (shape != expectedShape || plan.Series.Any(s => !s.Values.SequenceEqual(InSeries(journal, s.Org, s.Eskd), StringComparer.Ordinal)))
                return RefuseBatch(plan.Rows.Length, ["Серия изменилась после запроса подтверждения. Регистрация не выполнена. Отправьте весь список заново."]);
            return WriteBatch(context, repo, shape, plan);
        });

    private static ActionResult WriteBatch(ActionContext context, ConstructorRepository repo, RegistryShape shape, BatchPlan plan)
    {
        // Ошибки writer должны выйти исключением за границу scope: Failed сам по себе
        // транзакцию не откатывает. RegisterAll сохраняет вместе строки, поля и историю.
        var count = DocRegistrar.RegisterAll(repo, shape, plan.Rows, context.Now.LocalDateTime);
        return new ActionResult(true, $"Зарегистрировано: {count} из {plan.Rows.Length}. {plan.Common.Project} {plan.Common.Fio}",
            Rows: count, Reply: BatchCard(plan, $"Зарегистрировано: {count} из {plan.Rows.Length}."));
    }

    private static string BatchCard(BatchPlan plan, string heading) =>
        ReplyMarkup.Bold(ReplyMarkup.Escape(heading)) + "\n"
        + string.Join("\n", plan.Rows.Select(r => $"{r.Line}. {ReplyMarkup.Bold(ReplyMarkup.Escape(r.Designation))}")) + "\n"
        + $"Наименование: {ReplyMarkup.Escape(plan.Common.Name)}\nПроект: {ReplyMarkup.Escape(plan.Common.Project)}\nФИО: {ReplyMarkup.Escape(plan.Common.Fio)}";

    private static ActionResult RefuseBatch(int count, IEnumerable<string> problems)
    {
        var heading = $"Заявка отклонена. Зарегистрировано: 0 из {count}.";
        var details = string.Join("\n", problems);
        return new ActionResult(false, heading + "\n" + details,
            Reply: ReplyMarkup.Bold(heading) + "\n\n" + ReplyMarkup.Problems(details));
    }
}
