using SmartBase.Integrations;

namespace SmartBase.Database;

/// <summary>
/// Чем кончился разбор набора настроек.
/// </summary>
/// <param name="Connections">Соединений заведено.</param>
/// <param name="Requests">Запросов заведено.</param>
/// <param name="Scenarios">Сценариев заведено.</param>
/// <param name="Notes">На что обратить внимание: чего не нашлось, что переименовано, где нужен
/// секрет. Список не «ошибки», а то, что человек обязан доделать руками, — и молчать об этом
/// нельзя: приехавший и не работающий сценарий выглядит как поломка программы.</param>
public sealed record ImportReport(int Connections, int Requests, int Scenarios,
                                  IReadOnlyList<string> Notes)
{
    /// <summary>Итог одной фразой.</summary>
    public string Summary() =>
        $"заведено: сценариев {Scenarios}, запросов {Requests}, соединений {Connections}";
}

/// <summary>
/// Перенос настроек обмена между машинами: выгрузка сценариев в файл и разбор такого файла
/// (docs/ROADMAP-API.md, раздел 3 и шаг 9).
///
/// Формат и его разбор живут в <see cref="ScenarioBundle"/>; здесь — связывание имен с ключами
/// ЭТОЙ машины, и в этом вся работа: у соседа запрос №3 совсем другой, а имя «Заказы за сутки»
/// человек видит на экране и может сверить глазами.
///
/// <b>Четыре правила разбора, и все четыре — про то, чтобы не испортить уже работающее.</b>
///
/// <list type="number">
///   <item><b>Существующее не переписывается.</b> Соединение с таким именем уже есть — берем
///   его; у него есть секрет, которого в файле нет, и перезапись отняла бы у человека рабочий
///   обмен ради чужой карточки.</item>
///   <item><b>Сценарий-тезка не заменяется, а приезжает рядом</b> под свободным именем. Молча
///   заменить чужую настройку, которая ходит по расписанию, — худшее, что может сделать
///   разбор файла.</item>
///   <item><b>Приехавший сценарий выключен.</b> Всегда: файл открывают, чтобы посмотреть, а не
///   чтобы немедленно начать обмен со сторонней системой.</item>
///   <item><b>Чего не нашлось — названо.</b> База, коллекция, расширение: шаг приедет
///   ненастроенным, и это честнее, чем не привезти его вовсе.</item>
/// </list>
/// </summary>
public static class ScenarioTransfer
{
    // ── Выгрузка ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Собрать набор из сценариев. Вместе с ними уезжают запросы, на которые они ссылаются, и
    /// соединения этих запросов: сценарий без них на той стороне не соберется, а искать их по
    /// переписке — то же, что настраивать заново.
    /// </summary>
    public static ScenarioBundle Export(IntegrationsRegistry registry,
                                        IReadOnlyList<Scenario> scenarios,
                                        IReadOnlyList<DatabaseManager> databases,
                                        string machine, DateTimeOffset now)
    {
        var requests = new List<IntegrationRequest>();

        foreach (var step in scenarios.SelectMany(s => s.Steps))
        {
            if (step.Kind != ScenarioStepKind.Request) continue;
            if (requests.Any(r => r.Id == step.RequestId)) continue;
            if (registry.FindRequest(step.RequestId) is { } request) requests.Add(request);
        }

        // Запрос ответа на событие — такая же часть сценария, как шаг, и потоки, которые эти
        // сценарии заводят, тоже: без них на той стороне приедет сценарий, которому не с чего
        // начаться и нечем ответить.
        foreach (var scenario in scenarios.Where(s => s.Trigger.Replies))
        {
            if (requests.Any(r => r.Id == scenario.Trigger.RequestId)) continue;
            if (registry.FindRequest(scenario.Trigger.RequestId) is { } reply) requests.Add(reply);
        }

        var streams = scenarios
            .Where(s => s.Trigger.WatchesStream)
            .Select(s => s.Trigger.StreamId)
            .Distinct()
            .Select(registry.FindStream)
            .OfType<IntegrationStream>()
            .ToList();

        var connections = requests
            .Select(r => r.ConnectionId)
            .Concat(streams.Select(s => s.ConnectionId))
            .Distinct()
            .Select(registry.Find)
            .OfType<IntegrationConnection>()
            .ToList();

        var packed = streams.Select(s => Pack(s, connections)).ToList();

        return new ScenarioBundle(
            ScenarioBundle.VersionFor(packed),
            now.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            machine,
            [.. connections.Select(Pack)],
            [.. requests.Select(r => Pack(r, connections))],
            [.. scenarios.Select(s => Pack(s, requests, streams, databases))],
            packed.Count > 0 ? packed : null);
    }

    private static BundleStream Pack(IntegrationStream stream,
                                     IReadOnlyList<IntegrationConnection> connections) =>
        new(stream.Name,
            connections.FirstOrDefault(c => c.Id == stream.ConnectionId)?.Name ?? "",
            stream.Path,
            stream.Protocol.ToString().ToLowerInvariant(),
            stream.Subscriptions,
            stream.ItemsPath,
            stream.KeyField);

    private static BundleConnection Pack(IntegrationConnection connection) =>
        new(connection.Name,
            connection.Address,
            connection.Auth.ToString().ToLowerInvariant(),
            connection.AuthName,
            [.. connection.Headers.Select(h => new BundleHeader(h.Name, h.Value))],
            connection.TimeoutSeconds,
            connection.Retries);

    private static BundleRequest Pack(IntegrationRequest request,
                                      IReadOnlyList<IntegrationConnection> connections) =>
        new(request.Name,
            connections.FirstOrDefault(c => c.Id == request.ConnectionId)?.Name ?? "",
            request.Method.ToString().ToLowerInvariant(),
            request.Path,
            [.. request.Headers.Select(h => new BundleHeader(h.Name, h.Value))],
            request.Body,
            request.Paging.ToString().ToLowerInvariant(),
            request.PagingField,
            request.ItemsPath);

    private static BundleScenario Pack(Scenario scenario, IReadOnlyList<IntegrationRequest> requests,
                                       IReadOnlyList<IntegrationStream> streams,
                                       IReadOnlyList<DatabaseManager> databases) =>
        new(scenario.Name,
            new BundleTrigger(
                scenario.Trigger.Kind.ToString().ToLowerInvariant(),
                scenario.Trigger.Minutes,
                scenario.Trigger.Time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                NameOfDatabase(databases, scenario.Trigger.DbId),
                NameOfCollection(databases, scenario.Trigger.DbId, scenario.Trigger.CollectionId),
                streams.FirstOrDefault(s => s.Id == scenario.Trigger.StreamId)?.Name ?? "",
                scenario.Trigger.Replies
                    ? requests.FirstOrDefault(r => r.Id == scenario.Trigger.RequestId)?.Name ?? ""
                    : "",
                scenario.Trigger.FailText,
                scenario.Trigger.ReplyBreak),
            [.. scenario.Steps.Select(step => new BundleStep(
                step.Kind.ToString().ToLowerInvariant(),
                requests.FirstOrDefault(r => r.Id == step.RequestId)?.Name ?? "",
                step.Text,
                step.Extension,
                step.Action,
                NameOfDatabase(databases, step.DbId),
                NameOfCollection(databases, step.DbId, step.CollectionId),
                step.FieldValues,
                step.Folder,
                step.Conditions,
                step.Format,
                step.ColumnFormulas))]);

    private static string NameOfDatabase(IReadOnlyList<DatabaseManager> databases, int dbId) =>
        dbId <= 0 ? "" : databases.FirstOrDefault(d => d.DbId == dbId)?.Name ?? "";

    private static string NameOfCollection(IReadOnlyList<DatabaseManager> databases,
                                           int dbId, int collectionId)
    {
        if (dbId <= 0 || collectionId <= 0) return "";

        return databases.FirstOrDefault(d => d.DbId == dbId)?.Constructor
                        .CollectionName(collectionId) ?? "";
    }

    // ── Разбор ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Завести настройки из набора на этой машине. Исключений не бросает по мелочам: то, что не
    /// сошлось, попадает в заметки отчета, а остальное заводится — «ничего не привезли, потому
    /// что одна коллекция называется иначе» человеку не помогает.
    /// </summary>
    public static ImportReport Import(ScenarioBundle bundle, IntegrationsRegistry registry,
                                      IReadOnlyList<DatabaseManager> databases, DateTimeOffset now)
    {
        var notes = new List<string>();

        var connections = MergeConnections(bundle, registry, notes, out var madeConnections);
        var requests    = MergeRequests(bundle, registry, connections, notes, out var madeRequests);
        var streams     = MergeStreams(bundle, registry, connections, notes);
        var scenarios   = MergeScenarios(bundle, registry, requests, streams, databases, notes);

        return new ImportReport(madeConnections, madeRequests, scenarios, notes);
    }

    /// <summary>
    /// Имя потока → ключ на этой машине. Заведенные потоки названы в заметках отчета, а не
    /// своей цифрой: отчет считает то, что человек видит на трех прежних вкладках, и четвертая
    /// цифра ради редкого случая изменила бы его вид для всех.
    /// </summary>
    private static Dictionary<string, int> MergeStreams(
        ScenarioBundle bundle, IntegrationsRegistry registry,
        Dictionary<string, int> connections, List<string> notes)
    {
        var byName = registry.Streams()
            .GroupBy(s => s.Name.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.CurrentCultureIgnoreCase);

        foreach (var incoming in bundle.StreamList)
        {
            var name = (incoming.Name ?? "").Trim();
            if (name.Length == 0) continue;

            if (byName.ContainsKey(name))
            {
                notes.Add($"Поток событий «{name}» уже заведен — оставлен как был.");
                continue;
            }

            if (!connections.TryGetValue((incoming.Connection ?? "").Trim(), out var connectionId))
            {
                notes.Add($"Поток событий «{name}» не заведен: не нашлось соединения "
                        + $"«{incoming.Connection}».");
                continue;
            }

            var stream = IntegrationStream.New() with
            {
                Name         = name,
                ConnectionId = connectionId,
                Path         = string.IsNullOrWhiteSpace(incoming.Path) ? "/" : incoming.Path,
                Protocol     = (incoming.Protocol ?? "").Trim().ToLowerInvariant() == "stomp"
                    ? StreamProtocol.Stomp
                    : StreamProtocol.Plain,
                Topics       = [.. incoming.Topics ?? []],
                ItemsPath    = incoming.ItemsPath ?? "",
                KeyField     = incoming.KeyField ?? "",
            };

            if (stream.Problems().Count > 0)
            {
                notes.Add($"Поток событий «{name}» не заведен: в файле не хватает обязательного.");
                continue;
            }

            byName[name] = registry.SaveStream(stream).Id;
            notes.Add($"Заведен поток событий «{name}».");
        }

        return byName;
    }

    /// <summary>Имя соединения → ключ на этой машине.</summary>
    private static Dictionary<string, int> MergeConnections(
        ScenarioBundle bundle, IntegrationsRegistry registry, List<string> notes, out int made)
    {
        made = 0;

        var byName = registry.Connections()
            .GroupBy(c => c.Name.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.CurrentCultureIgnoreCase);

        foreach (var incoming in bundle.Connections)
        {
            var name = (incoming.Name ?? "").Trim();
            if (name.Length == 0) continue;

            if (byName.ContainsKey(name))
            {
                // Существующее не переписываем: у здешней карточки есть секрет, которого в
                // файле нет, и перезапись отняла бы у человека рабочий обмен.
                notes.Add($"Соединение «{name}» уже заведено — оставлено как было.");
                continue;
            }

            var connection = IntegrationConnection.New() with
            {
                Name           = name,
                Address        = incoming.Address ?? "",
                Auth           = ParseAuth(incoming.Auth),
                AuthName       = incoming.AuthName ?? "",
                Headers        = [.. (incoming.Headers ?? []).Select(h => new IntegrationHeader(h.Name, h.Value))],
                TimeoutSeconds = incoming.TimeoutSeconds > 0
                    ? incoming.TimeoutSeconds
                    : IntegrationConnection.DefaultTimeoutSeconds,
                Retries        = Math.Clamp(incoming.Retries, 0, IntegrationConnection.MaxRetries),
            };

            if (connection.Problems().Count > 0)
            {
                notes.Add($"Соединение «{name}» не заведено: в файле не хватает обязательного.");
                continue;
            }

            var saved = registry.Save(connection);
            byName[name] = saved.Id;
            made++;

            if (saved.NeedsSecret)
                notes.Add($"Соединению «{name}» нужен секрет: в файле его нет и быть не может. "
                        + "Впишите токен или пароль в его карточке.");
        }

        return byName;
    }

    /// <summary>Имя запроса → ключ на этой машине.</summary>
    private static Dictionary<string, int> MergeRequests(
        ScenarioBundle bundle, IntegrationsRegistry registry,
        Dictionary<string, int> connections, List<string> notes, out int made)
    {
        made = 0;

        var byName = registry.Requests()
            .GroupBy(r => r.Name.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.CurrentCultureIgnoreCase);

        foreach (var incoming in bundle.Requests)
        {
            var name = (incoming.Name ?? "").Trim();
            if (name.Length == 0) continue;

            if (byName.ContainsKey(name))
            {
                notes.Add($"Запрос «{name}» уже заведен — оставлен как был.");
                continue;
            }

            if (!connections.TryGetValue((incoming.Connection ?? "").Trim(), out var connectionId))
            {
                notes.Add($"Запрос «{name}» не заведен: не нашлось соединения "
                        + $"«{incoming.Connection}».");
                continue;
            }

            var request = IntegrationRequest.New() with
            {
                Name         = name,
                ConnectionId = connectionId,
                Method       = ParseMethod(incoming.Method),
                Path         = string.IsNullOrWhiteSpace(incoming.Path) ? "/" : incoming.Path,
                Headers      = [.. (incoming.Headers ?? []).Select(h => new IntegrationHeader(h.Name, h.Value))],
                Body         = incoming.Body ?? "",
                Paging       = ParsePaging(incoming.Paging),
                PagingField  = incoming.PagingField ?? "",
                ItemsPath    = incoming.ItemsPath ?? "",
            };

            if (request.Problems().Count > 0)
            {
                notes.Add($"Запрос «{name}» не заведен: в файле не хватает обязательного.");
                continue;
            }

            byName[name] = registry.SaveRequest(request).Id;
            made++;
        }

        return byName;
    }

    private static int MergeScenarios(ScenarioBundle bundle, IntegrationsRegistry registry,
                                      Dictionary<string, int> requests,
                                      Dictionary<string, int> streams,
                                      IReadOnlyList<DatabaseManager> databases,
                                      List<string> notes)
    {
        var taken = new HashSet<string>(registry.Scenarios().Select(s => s.Name.Trim()),
                                        StringComparer.CurrentCultureIgnoreCase);
        var made  = 0;

        foreach (var incoming in bundle.Scenarios)
        {
            var wanted = (incoming.Name ?? "").Trim();
            if (wanted.Length == 0) continue;

            var name = FreeName(wanted, taken);
            if (!string.Equals(name, wanted, StringComparison.Ordinal))
                notes.Add($"Сценарий «{wanted}» уже есть — приехавший заведен как «{name}».");

            var steps = new List<ScenarioStep>();

            foreach (var step in incoming.Steps ?? [])
                if (Unpack(step, requests, databases, name, notes) is { } made_step)
                    steps.Add(made_step);

            var scenario = new Scenario(0, name, false,
                                        Unpack(incoming.Trigger, requests, streams, databases,
                                               name, notes),
                                        steps);

            if (scenario.Problems().Count > 0)
            {
                notes.Add($"Сценарий «{name}» не заведен: "
                        + string.Join("; ", scenario.Problems()) + ".");
                continue;
            }

            registry.SaveScenario(scenario);
            taken.Add(name);
            made++;
        }

        if (made > 0)
            notes.Add("Приехавшие сценарии выключены: включите их, когда проверите настройки.");

        return made;
    }

    private static ScenarioStep? Unpack(BundleStep step, Dictionary<string, int> requests,
                                        IReadOnlyList<DatabaseManager> databases,
                                        string scenario, List<string> notes)
    {
        var kind = ParseStepKind(step.Kind);

        var requestId = 0;

        if (kind == ScenarioStepKind.Request)
        {
            if (!requests.TryGetValue((step.Request ?? "").Trim(), out requestId))
            {
                // Шаг без запроса сохранить нельзя — проверка формы его не пропустит, а
                // молчаливо привезти сценарий без шага значило бы отдать человеку обмен,
                // который делает не то, что у соседа.
                notes.Add($"Сценарий «{scenario}»: шаг с запросом «{step.Request}» не привезен — "
                        + "такого запроса на этой машине нет.");
                return null;
            }
        }

        var (dbId, collectionId) = Resolve(databases, step.Database, step.Collection);

        if (collectionId == 0 && (step.Collection ?? "").Trim().Length > 0)
            notes.Add($"Сценарий «{scenario}»: коллекция «{step.Collection}» "
                    + $"в базе «{step.Database}» не нашлась — шаг приехал ненастроенным.");

        return new ScenarioStep(
            kind,
            requestId,
            step.Text ?? "",
            step.Extension ?? "",
            step.Action ?? "",
            dbId,
            collectionId,
            step.Fields,
            step.Folder ?? "",
            step.Conditions,
            step.Format ?? "",
            step.Formulas);
    }

    private static ScenarioTrigger Unpack(BundleTrigger? trigger,
                                          Dictionary<string, int> requests,
                                          Dictionary<string, int> streams,
                                          IReadOnlyList<DatabaseManager> databases,
                                          string scenario, List<string> notes)
    {
        if (trigger is null) return ScenarioTrigger.Manual;

        switch ((trigger.Kind ?? "").Trim().ToLowerInvariant())
        {
            case "stream":
                if (!streams.TryGetValue((trigger.Stream ?? "").Trim(), out var streamId))
                {
                    notes.Add($"Сценарий «{scenario}»: поток событий «{trigger.Stream}» не "
                            + "нашелся — он приехал ручным.");
                    return ScenarioTrigger.Manual;
                }

                var reply = (trigger.Reply ?? "").Trim();

                if (reply.Length == 0) return ScenarioTrigger.OnStream(streamId);

                if (requests.TryGetValue(reply, out var replyId))
                    return ScenarioTrigger.OnStream(streamId, replyId, trigger.FailText ?? "",
                                                    trigger.ReplyBreak ?? "");

                // Без запроса ответа сценарий примет событие и промолчит. Привезти его таким
                // можно, но сказать об этом надо: на той машине он отвечал.
                notes.Add($"Сценарий «{scenario}»: запрос ответа «{trigger.Reply}» не нашелся — "
                        + "сценарий приехал без ответа на событие.");
                return ScenarioTrigger.OnStream(streamId);

            case "every":
                return ScenarioTrigger.EveryMinutes(
                    Math.Clamp(trigger.Minutes, ScenarioTrigger.MinMinutes, ScenarioTrigger.MaxMinutes));

            case "daily":
                return TimeOnly.TryParseExact(trigger.Time, "HH:mm",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var moment)
                    ? ScenarioTrigger.DailyAt(moment)
                    : ScenarioTrigger.Manual;

            case "changed":
                var (dbId, collectionId) = Resolve(databases, trigger.Database, trigger.Collection);

                if (dbId > 0 && collectionId > 0)
                    return ScenarioTrigger.OnChange(dbId, collectionId,
                        Math.Clamp(trigger.Minutes, ScenarioTrigger.MinMinutes, ScenarioTrigger.MaxMinutes));

                notes.Add($"Сценарий «{scenario}»: коллекция триггера «{trigger.Collection}» "
                        + $"в базе «{trigger.Database}» не нашлась — он приехал ручным.");
                return ScenarioTrigger.Manual;

            case "calendar":
                var (eventDb, eventCollection) = Resolve(databases, trigger.Database, trigger.Collection);

                if (eventDb > 0 && eventCollection > 0)
                    return ScenarioTrigger.BeforeEvent(eventDb, eventCollection,
                        Math.Clamp(trigger.Minutes, 0, ScenarioTrigger.MaxMinutes));

                notes.Add($"Сценарий «{scenario}»: коллекция триггера «{trigger.Collection}» "
                        + $"в базе «{trigger.Database}» не нашлась — он приехал ручным.");
                return ScenarioTrigger.Manual;

            default:
                return ScenarioTrigger.Manual;
        }
    }

    /// <summary>База и коллекция по именам; 0 — не нашлось.</summary>
    private static (int DbId, int CollectionId) Resolve(IReadOnlyList<DatabaseManager> databases,
                                                        string? database, string? collection)
    {
        var wantedDb = (database ?? "").Trim();
        if (wantedDb.Length == 0) return (0, 0);

        var target = databases.FirstOrDefault(d =>
            string.Equals(d.Name.Trim(), wantedDb, StringComparison.CurrentCultureIgnoreCase));

        if (target is null) return (0, 0);

        var wantedCollection = (collection ?? "").Trim();
        if (wantedCollection.Length == 0) return (target.DbId, 0);

        var found = target.Constructor.GetAllCollections()
            .FirstOrDefault(c => string.Equals((c.Name ?? "").Trim(), wantedCollection,
                                               StringComparison.CurrentCultureIgnoreCase));

        return (target.DbId, found?.Id ?? 0);
    }

    /// <summary>Свободное имя: «Заказы» → «Заказы (2)». Тезку не заменяем никогда.</summary>
    private static string FreeName(string wanted, HashSet<string> taken)
    {
        if (!taken.Contains(wanted)) return wanted;

        for (var number = 2; number < 100; number++)
        {
            var candidate = $"{wanted} ({number})";
            if (!taken.Contains(candidate)) return candidate;
        }

        return $"{wanted} ({Guid.NewGuid():N})"[..Math.Min(60, wanted.Length + 10)];
    }

    // ── Разбор перечислений ───────────────────────────────────────────────────
    //
    // Незнакомое значение читается как самое безобидное, а не как отказ: строку мог написать
    // более новый выпуск программы, и терять из-за нее весь набор незачем.

    private static ConnectionAuth ParseAuth(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "header" => ConnectionAuth.Header,
        "bearer" => ConnectionAuth.Bearer,
        "basic"  => ConnectionAuth.Basic,
        "query"  => ConnectionAuth.Query,
        _        => ConnectionAuth.None,
    };

    private static RequestMethod ParseMethod(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "post"   => RequestMethod.Post,
        "put"    => RequestMethod.Put,
        "patch"  => RequestMethod.Patch,
        "delete" => RequestMethod.Delete,
        _        => RequestMethod.Get,
    };

    private static RequestPaging ParsePaging(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "page"   => RequestPaging.Page,
        "offset" => RequestPaging.Offset,
        "cursor" => RequestPaging.Cursor,
        _        => RequestPaging.None,
    };

    private static ScenarioStepKind ParseStepKind(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "request"      => ScenarioStepKind.Request,
        "action"       => ScenarioStepKind.Action,
        "intake"       => ScenarioStepKind.Intake,
        "filter"       => ScenarioStepKind.Filter,
        "createrecord" => ScenarioStepKind.CreateRecord,
        "export"       => ScenarioStepKind.Export,
        "compute"      => ScenarioStepKind.Compute,
        _              => ScenarioStepKind.Notify,
    };
}
