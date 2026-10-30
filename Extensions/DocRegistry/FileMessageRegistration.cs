using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmartBase.Database.Repositories;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>Книга из сообщения: общая проверка, одно подтверждение и постоянная квитанция записи.</summary>
internal static partial class DocumentMessages
{
    // На одну открытую базу: данные всех ожидающих файлов, включая снимки занятых серий.
    private const long MaxPendingFileCharacters = 4L * 1024 * 1024;
    private sealed record FileRegistrationPlan(string FileName, string ReceiptKey, string Digest,
                                              ImportRow[] Rows, SeriesSnapshot[] Series);
    private sealed record FileReceipt(string Digest, string Reply);

    public static ActionResult RegisterFile(ActionContext context, byte[] bytes, string fileName,
                                            string sender, string messageId)
    {
        var ct = context.CancellationToken;
        ct.ThrowIfCancellationRequested();
        if (messageId.Length == 0)
            return ActionResult.Failed("для регистрации файла не заполнено поле «Идентификатор сообщения с файлом»");
        if (messageId.Length > 1024 || messageId.Any(char.IsControl))
            return ReplyMarkup.Refused("Файл: неверный идентификатор сообщения");
        if (context.Repo is not { } repo)
            return ActionResult.Failed("действию не дали подключения к базе данных");

        var receiptKey = FileKey(context, sender, messageId);
        var digest = Convert.ToHexString(SHA256.HashData(bytes));
        if (FileReplay(repo, receiptKey, digest) is { } replay) return replay;
        var initialCheck = Shape(repo, context.CollectionId);
        if (initialCheck.Shape is null) return ActionResult.Failed(initialCheck.Problem!);

        IReadOnlyList<ImportRow> source;
        string name;
        try
        {
            name = RegistryMessageFile.SafeName(fileName);
            source = RegistryMessageFile.Read(bytes, name, ct);
        }
        catch (DocRegistryException error)
        {
            return RefuseFile(0, [error.Message]);
        }

        var orgs = DocRegistryExtension.ParseOrgCodes(context.Setting(DocRegistryExtension.OrgCodesKey));
        var items = source.Select(row =>
        {
            ct.ThrowIfCancellationRequested();
            var (request, problems) = MessageRegistration.ReadValues(
            [
                (MessageRegistration.ActionKey, MessageRegistration.RegisterWord),
                (MessageRegistration.NumberKey, row.Designation),
                (MessageRegistration.NameKey, row.Name),
                (MessageRegistration.ProjectKey, row.Project),
                (MessageRegistration.FioKey, row.Fio),
            ], orgs);
            return (Row: row, Request: request, Problems: problems);
        }).ToArray();

        var waiting = WaitingByRepo.GetValue(context.Session ?? repo, _ => new Waiting());
        var key = (context.DbId, context.CollectionId, context.Channel, sender.ToUpperInvariant());
        lock (waiting)
        {
            if (waiting.Items.TryGetValue(key, out var active) && active.Expires > context.Now)
            {
                if (active.File is { } same && same.ReceiptKey == receiptKey)
                    return same.Digest == digest ? FilePreview(same)
                        : RefuseFile(source.Count, ["Файл: сообщение с этим идентификатором уже ожидает подтверждения с другим содержимым."]);
                return ReplyMarkup.RefusedHeaded("Ожидается подтверждение. Для нового запроса сначала ответьте Нет");
            }
            try
            {
                return repo.InCollectionWriteTransaction(context.CollectionId, () =>
                {
                    ct.ThrowIfCancellationRequested();
                    if (FileReplay(repo, receiptKey, digest) is { } repeated) return repeated;
                    var check = Shape(repo, context.CollectionId);
                    if (check.Shape is not { } shape) return ActionResult.Failed(check.Problem!);
                    var journal = DocRegistrar.Designations(repo, shape);
                    var snapshots = FileSeries(journal, items.Where(item => item.Request is not null)
                        .Select(item => (item.Request!.Org, item.Request.Eskd)), ct);
                    var occupied = snapshots.ToDictionary(series => (series.Org, series.Eskd), series => series.Values.ToList());
                    var rows = new List<ImportRow>();
                    var problems = new List<string>();
                    foreach (var item in items)
                    {
                        ct.ThrowIfCancellationRequested();
                        var sourceDesignation = item.Row.Designation.Length > 0 ? item.Row.Designation : "(пустой номер)";
                        var prefix = $"Строка {item.Row.Line}: {sourceDesignation} — ";
                        if (item.Request is not { } request || item.Problems.Count > 0)
                        {
                            problems.Add(prefix + string.Join("; ", item.Problems));
                            continue;
                        }
                        var values = occupied[(request.Org, request.Eskd)];
                        var (designation, problem) = Allocate(request, values);
                        if (designation is null)
                        {
                            problems.Add(prefix + problem);
                            continue;
                        }
                        values.Add(designation);
                        rows.Add(item.Row with
                        {
                            Name = request.Name, Designation = designation, Project = request.Project, Fio = request.Fio,
                        });
                    }
                    if (problems.Count > 0)
                        return RefuseFile(source.Count, [$"Файл: {name}", .. problems,
                            "Исправьте ошибки и отправьте весь файл заново."]);
                    var plan = new FileRegistrationPlan(name, receiptKey, digest, rows.ToArray(), snapshots);
                    if (snapshots.All(series => series.Values.Length > 0)) return WriteFile(context, repo, shape, plan);

                    foreach (var expired in waiting.Items.Where(pair => pair.Value.Expires <= context.Now).Select(pair => pair.Key).ToArray())
                        waiting.Items.Remove(expired);
                    if (waiting.Items.Count >= 256)
                        return RefuseFile(source.Count, ["Подтверждение: слишком много ожидающих операций. Повторите запрос позже"]);
                    var characters = FileCharacters(plan);
                    if (characters > MaxPendingFileCharacters)
                        return RefuseFile(source.Count,
                            ["Файл и снимки серий содержат слишком много данных для ожидания подтверждения. Разделите список на меньшие файлы."]);
                    if (waiting.Items.Values.Where(pending => pending.File is not null)
                            .Sum(pending => FileCharacters(pending.File!)) > MaxPendingFileCharacters - characters)
                        return RefuseFile(source.Count,
                            ["Подтверждение: слишком много данных в ожидающих файлах. Сначала завершите прежние операции ответом Да или Нет."]);
                    var card = new Card(0, name, "", "", "", "", "", "", "", null);
                    waiting.Items[key] = new Pending(MessageRegistration.RegisterWord, card, shape,
                        new Dictionary<string, string>(), context.Now + Lifetime, File: plan);
                    return FilePreview(plan);
                });
            }
            catch (DocRegistryException error)
            {
                return RefuseFile(source.Count, [error.Message, "Отправьте весь файл заново."]);
            }
        }
    }

    private static ActionResult ConfirmFile(ActionContext context, ConstructorRepository repo,
                                             RegistryShape expectedShape, FileRegistrationPlan plan) =>
        repo.InCollectionWriteTransaction(context.CollectionId, () =>
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (FileReplay(repo, plan.ReceiptKey, plan.Digest) is { } replay) return replay;
            var check = Shape(repo, context.CollectionId);
            if (check.Shape is not { } shape || shape != expectedShape)
                return RefuseFile(plan.Rows.Length, ["Структура журнала изменилась. Отправьте весь файл заново."]);
            var orgs = DocRegistryExtension.ParseOrgCodes(context.Setting(DocRegistryExtension.OrgCodesKey));
            if (orgs.Count > 0 && plan.Series.Any(series =>
                    !orgs.Any(org => MessageRegistration.Org(org) == MessageRegistration.Org(series.Org))))
                return RefuseFile(plan.Rows.Length, ["Номер: код организации больше не разрешен. Отправьте весь файл заново."]);
            var journal = DocRegistrar.Designations(repo, shape);
            var current = FileSeries(journal, plan.Series.Select(series => (series.Org, series.Eskd)), context.CancellationToken);
            if (plan.Series.Where((series, index) => !series.Values.SequenceEqual(current[index].Values, StringComparer.Ordinal)).Any())
                return RefuseFile(plan.Rows.Length,
                    ["Серия изменилась после запроса подтверждения. Регистрация не выполнена. Отправьте весь файл заново."]);
            return WriteFile(context, repo, shape, plan);
        });

    private static ActionResult WriteFile(ActionContext context, ConstructorRepository repo,
                                          RegistryShape shape, FileRegistrationPlan plan)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        // Квитанция коммитится вместе со строками. Сбой сохранения квитанции должен выйти
        // исключением из внешнего scope, чтобы после перезапуска не выдать номера повторно.
        var count = DocRegistrar.RegisterAll(repo, shape, plan.Rows, context.Now.LocalDateTime,
            ct: context.CancellationToken);
        context.CancellationToken.ThrowIfCancellationRequested();
        var reply = FileCard(plan, $"Зарегистрировано: {count} из {plan.Rows.Length}.");
        repo.WriteExtensionState(DocRegistryExtension.ExtensionId, plan.ReceiptKey,
            JsonSerializer.Serialize(new FileReceipt(plan.Digest, reply)));
        context.CancellationToken.ThrowIfCancellationRequested();
        return new ActionResult(true, $"Файл «{plan.FileName}»: зарегистрировано {count} из {plan.Rows.Length}",
            Rows: count, Reply: reply);
    }

    private static ActionResult FilePreview(FileRegistrationPlan plan)
    {
        var warnings = string.Join("\n\n", plan.Series.Where(series => series.Values.Length == 0).Select(FirstSeriesWarning));
        return Reply(FileCard(plan, $"Заявка на регистрацию: {plan.Rows.Length}") + "\n\n" + warnings + "\n\n"
            + ReplyMarkup.Bold("Подтверждение относится ко всему файлу. До ответа Да ничего не зарегистрировано.")
            + "\n" + Prompt, 0);
    }

    private static string FileCard(FileRegistrationPlan plan, string heading) =>
        ReplyMarkup.Bold(ReplyMarkup.Escape(heading)) + "\nФайл: " + ReplyMarkup.Escape(plan.FileName) + "\n"
        + string.Join("\n", plan.Rows.Select(row =>
            $"Строка {row.Line}: {ReplyMarkup.Bold(ReplyMarkup.Escape(row.Designation))}"));

    private static ActionResult RefuseFile(int count, IEnumerable<string> problems)
    {
        if (count > 0) return RefuseBatch(count, problems);
        // Файл еще не прочитан: число строк неизвестно, «0 из 0» выдало бы догадку за результат.
        const string heading = "Заявка отклонена. Зарегистрировано: 0.";
        var details = string.Join("\n", problems);
        return new ActionResult(false, heading + "\n" + details,
            Reply: ReplyMarkup.Bold(heading) + "\n\n" + ReplyMarkup.Problems(details));
    }

    private static long FileCharacters(FileRegistrationPlan plan) =>
        (long)plan.FileName.Length + plan.ReceiptKey.Length + plan.Digest.Length
        + plan.Rows.Sum(row => (long)row.Name.Length + row.Designation.Length + row.Fio.Length + row.Project.Length
            + row.Accounting.Length + row.FirstUse.Length + row.Summary.Length)
        + plan.Series.Sum(series => (long)series.Org.Length + series.Eskd.Length + series.Values.Sum(value => (long)value.Length));

    private static SeriesSnapshot[] FileSeries(IEnumerable<string> journal,
                                               IEnumerable<(string Org, string Eskd)> requested,
                                               CancellationToken ct)
    {
        var series = requested.Distinct().ToArray();
        var groups = series.ToDictionary(pair => Designation.Prefix(pair.Org, pair.Eskd),
            pair => (pair.Org, pair.Eskd, Values: new List<string>()), StringComparer.CurrentCultureIgnoreCase);
        var lengths = groups.Keys.Select(prefix => prefix.Length).Distinct().ToArray();
        // Журнал обходится один раз, а не заново для каждой из тысячи строк/серий книги.
        foreach (var value in journal)
        {
            ct.ThrowIfCancellationRequested();
            var text = value.Trim();
            foreach (var length in lengths)
                if (text.Length > length && groups.TryGetValue(text[..length], out var group)
                    && Designation.NumberIn(value, group.Org, group.Eskd) is not null)
                    group.Values.Add(value);
        }
        return series.Select(pair => new SeriesSnapshot(pair.Org, pair.Eskd,
            groups[Designation.Prefix(pair.Org, pair.Eskd)].Values.OrderBy(value => value, StringComparer.Ordinal).ToArray())).ToArray();
    }

    private static string FileKey(ActionContext context, string sender, string messageId) =>
        "file:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[]
        {
            context.CollectionId.ToString(CultureInfo.InvariantCulture), context.Channel,
            sender.ToUpperInvariant(), messageId,
        }))));

    private static ActionResult? FileReplay(ConstructorRepository repo, string key, string digest)
    {
        var stored = repo.ReadExtensionState(DocRegistryExtension.ExtensionId, key);
        if (stored is null) return null;
        try
        {
            var receipt = JsonSerializer.Deserialize<FileReceipt>(stored);
            if (receipt is null || receipt.Digest is null || receipt.Digest.Length != 64 || string.IsNullOrEmpty(receipt.Reply))
                return ActionResult.Failed("не удалось прочитать результат прежней регистрации файла; повторная запись остановлена");
            if (receipt.Digest != digest)
                return RefuseFile(0, ["Файл: сообщение с этим идентификатором уже зарегистрировано с другим содержимым. Отправьте новый файл отдельным сообщением."]);
            return new ActionResult(true, "Повторная доставка: файл уже зарегистрирован, новые записи не созданы",
                Rows: 0, Reply: receipt.Reply);
        }
        catch (JsonException)
        {
            return ActionResult.Failed("не удалось прочитать результат прежней регистрации файла; повторная запись остановлена");
        }
    }
}
