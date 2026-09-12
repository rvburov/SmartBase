using System.Globalization;
using System.Text;
using SmartBase.Core.Models;
using SmartBase.Integrations;

namespace SmartBase.Database;

/// <summary>
/// Открытая база в паспорте: как ее звать и что на ней написано.
///
/// Не <c>DatabaseManager</c>, а короткая выписка из него: паспорт собирается из сведений, а не
/// из живых подключений, и так его можно составить в тесте, не открывая ни одной базы.
/// </summary>
/// <param name="Id">Ключ базы — тот же, что пишется строкой журнала.</param>
/// <param name="Name">Имя базы, как ее знает человек.</param>
/// <param name="Label">Метка конфиденциальности базы.</param>
/// <param name="LabeledFields">Сколько свойств в ней несут собственную метку.</param>
public sealed record PassportDatabase(
    int                  Id,
    string               Name,
    ConfidentialityLabel Label,
    int                  LabeledFields);

/// <summary>
/// Паспорт интеграций — один файл, который отдают службе безопасности
/// (docs/ROADMAP-API.md, 10.16).
///
/// Отвечает на четыре вопроса, и ровно в том порядке, в каком их задают: <b>что разрешено</b>
/// (политика организации), <b>с чем программа соединяется</b> (соединения и запросы), <b>кому
/// разрешено уносить данные и с чьего подтверждения</b> (направления), <b>что и сколько ушло за
/// период</b> (сводка по журналу безопасности).
///
/// <b>Простой текст, а не таблица и не JSON.</b> Этот документ читают глазами, печатают и
/// подшивают; разметка, которую надо чем-то открывать, тут мешает, а машине он не адресован.
///
/// <b>Ни одного значения поля.</b> Паспорт уезжает из организации так же легко, как журнал, и
/// сделан по тем же правилам: имена полей — да, значения — никогда, адреса — замаскированные, от
/// тел остались отпечатки.
///
/// <b>Последним разделом — чего этот документ НЕ доказывает.</b> Его составила та самая
/// программа, о которой он рассказывает, и умолчать об этом значило бы выдать сводку за
/// доказательство. Проверяется он по журналу безопасности, и так прямо в нем и написано.
///
/// Там же сказано, чего у программы НЕТ: копии журнала вне этой машины. Она названа — без нее
/// цепочка отпечатков не ловит подмену файла целиком, — но названа как то, чего программа не
/// делает. Назвать ее контролем, которого нет, значит подсунуть проверяющему опору, на которую
/// он обопрется: документ, врущий службе безопасности о собственных рубежах, хуже документа,
/// который не написали.
/// </summary>
public static class IntegrationsPassport
{
    /// <summary>За сколько дней берется сводка по журналу.</summary>
    public const int DefaultDays = 30;

    /// <summary>Сколько имен полей называем по одному направлению; остальные — числом.</summary>
    public const int MaxFields = 40;

    private const string Line = "────────────────────────────────────────────────────────────";

    /// <summary>Собрать паспорт целиком.</summary>
    /// <param name="registry">Соединения, запросы и подтверждения — из app.db.</param>
    /// <param name="policy">Что вернул разбор политики: набор правил и судьба каждого источника.</param>
    /// <param name="journal">Журнал безопасности; <c>null</c> — не открылся, и это само по себе
    /// сведение для проверяющего.</param>
    /// <param name="databases">Открытые базы и их метки.</param>
    /// <param name="who">Кто составляет.</param>
    /// <param name="now">Момент составления.</param>
    /// <param name="days">Глубина сводки в днях.</param>
    public static string Build(IntegrationsRegistry            registry,
                               PolicyLoadResult                policy,
                               SecurityJournal?                journal,
                               IReadOnlyList<PassportDatabase> databases,
                               string                          who,
                               DateTimeOffset                  now,
                               int                             days = DefaultDays)
    {
        var text  = new StringBuilder();
        var since = now.AddDays(-Math.Max(1, days));

        Head(text, who, now, since, days);
        Policy(text, policy.Set, policy.Reads, DateOnly.FromDateTime(now.LocalDateTime.Date));
        Connections(text, registry);
        Ingress(text, registry, databases);
        Directions(text, registry, now);
        Traffic(text, journal, since);
        Labels(text, databases);
        Journal(text, journal);
        Limits(text, journal, days);

        return text.ToString();
    }

    // ── Заголовок ─────────────────────────────────────────────────────────────

    private static void Head(StringBuilder text, string who, DateTimeOffset now,
                             DateTimeOffset since, int days)
    {
        text.AppendLine("ПАСПОРТ ИНТЕГРАЦИЙ");
        text.AppendLine(Line);
        text.AppendLine($"Составлен      {Moment(now)}");
        text.AppendLine($"Кто составил   {Text(who)}");
        text.AppendLine($"Компьютер      {Environment.MachineName}");
        text.AppendLine($"Период сводки  {days} дней, с {Day(since)}");
        text.AppendLine();
    }

    // ── 1. Политика ───────────────────────────────────────────────────────────

    private static void Policy(StringBuilder text, EgressPolicySet policy,
                               IReadOnlyList<PolicyReadResult> reads, DateOnly today)
    {
        Title(text, "1. ЧТО РАЗРЕШЕНО: ПОЛИТИКА ОРГАНИЗАЦИИ");

        if (EgressGate.ClosedLoopBuild)
        {
            text.AppendLine("  Сборка для закрытого контура: сетевого клиента в программе нет,");
            text.AppendLine("  исходящие обращения невозможны физически.");
            text.AppendLine();
        }

        if (!policy.Any)
        {
            text.AppendLine("  Политика не найдена — исходящие обращения запрещены целиком.");
            text.AppendLine("  Это состояние по умолчанию: без разрешения наружу не уходит ничего.");
        }
        else
        {
            text.AppendLine($"  Режим          {Mode(policy.Mode)}");
            text.AppendLine($"  Действует до   {(policy.ValidUntil is { } until ? until.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "без срока")}"
                          + (policy.Expired(today) ? "  — СРОК ВЫШЕЛ, обмен запрещен" : ""));
            text.AppendLine($"  Мастер-пароль  {(policy.HasMaster ? "задан" : "НЕ ЗАДАН — подтверждать направления нечем")}");
        }

        text.AppendLine();
        text.AppendLine("  Источники (разрешено то, что разрешают ВСЕ разом; непрочитанный");
        text.AppendLine("  источник запирает контур, а не выпадает из расчета):");

        if (reads.Count == 0) text.AppendLine("    ни одного источника не найдено");

        foreach (var read in reads)
            text.AppendLine($"    {(read.Accepted ? "принят  " : "отклонен")} {read.Source} — {read.Note}");

        if (policy.Any)
        {
            text.AppendLine();
            text.AppendLine("  Белый список по источникам:");

            foreach (var source in policy.Sources)
            {
                text.AppendLine($"    {source.Source}");
                text.AppendLine($"      имена  {Join(source.Hosts)}");
                text.AppendLine($"      сети   {Join(source.Networks)}");
                text.AppendLine($"      папки  {Join(source.Folders)}");
            }

            text.AppendLine();
            text.AppendLine("  Потолки (действует самый строгий из источников):");
            text.AppendLine($"    строк за запуск   не больше {policy.MaxRowsPerRun:N0}");
            text.AppendLine($"    объем в сутки     не больше {Size(policy.MaxBytesPerDay)}");
            text.AppendLine($"    запусков в час    не больше {policy.MaxRunsPerHour:N0}");
            text.AppendLine($"    адреса самому     {(policy.UserMayAddHosts ? "разрешено дописывать" : "дописывать запрещено")}");
        }

        text.AppendLine();
    }

    // ── 2. С чем соединяется ──────────────────────────────────────────────────

    private static void Connections(StringBuilder text, IntegrationsRegistry registry)
    {
        Title(text, "2. С ЧЕМ СОЕДИНЯЕТСЯ ПРОГРАММА");

        var connections = registry.Connections();

        if (connections.Count == 0)
        {
            text.AppendLine("  Ни одного соединения не заведено.");
            text.AppendLine();
            return;
        }

        foreach (var connection in connections)
        {
            text.AppendLine($"  «{connection.Name}»  {connection.Address}");
            text.AppendLine($"    вход: {Auth(connection)}"
                          + $" · таймаут {connection.TimeoutSeconds} с"
                          + $" · повторов {connection.Retries}");

            var requests = registry.RequestsOf(connection.Id);

            if (requests.Count == 0)
            {
                text.AppendLine("    запросов нет");
                continue;
            }

            foreach (var request in requests)
                text.AppendLine($"    {request.Method.ToString().ToUpperInvariant(),-6} {request.Path}"
                              + $"   «{request.Name}»"
                              + (request.Outward ? "  [уносит данные]" : "  [только читает]"));
        }

        text.AppendLine();
        text.AppendLine("  Секретов соединений в этом документе нет и быть не может: они лежат");
        text.AppendLine("  отдельно от файла базы и не показываются даже в самой программе.");
        text.AppendLine();
    }

    // ── 3. Кто может постучаться к нам ────────────────────────────────────────

    /// <summary>
    /// Входящий канал. Проверяющий спрашивает об открытом порте раньше, чем об исходящих, и
    /// ответ обязан быть в том же документе: слушаем ли, каким способом, кому выдан доступ и
    /// что этот доступ позволяет.
    ///
    /// Настройки приема берутся у машины, а не у базы: порт открывает конкретный компьютер, и
    /// паспорт составляется про него.
    /// </summary>
    private static void Ingress(StringBuilder text, IntegrationsRegistry registry,
                                IReadOnlyList<PassportDatabase> databases)
    {
        Title(text, "3. КТО МОЖЕТ ПОСТУЧАТЬСЯ К НАМ");

        var mode = Utils.AppSettings.LoadIngressMode();
        var port = Utils.AppSettings.LoadIngressPort();

        if (!IngressGate.ListenerBuild)
            text.AppendLine("  Сборка без слушателя: принимающего кода в ней нет, порт не открывается.");
        else
        {
            var decision = IngressGate.Check(mode, port);

            text.AppendLine(mode switch
            {
                IngressMode.Off      => "  Прием по сети выключен.",
                IngressMode.Loopback => $"  Прием только с этого компьютера: {IngressGate.LoopbackHost}:{port}.",
                _                    => $"  Прием по локальной сети, порт {port}.",
            });

            if (mode != IngressMode.Off && !decision.Allowed)
                text.AppendLine("  Сейчас не действует: " + decision.Explanation + ".");
        }

        text.AppendLine();

        var tokens = registry.Tokens();

        if (tokens.Count == 0)
            text.AppendLine("  Токенов доступа не выдано ни одного: постучаться к нам некому.");
        else
        {
            text.AppendLine($"  Токенов выдано: {tokens.Count}");
            text.AppendLine();

            foreach (var token in tokens)
            {
                var database = databases.FirstOrDefault(d => d.Id == token.DbId)?.Name
                            ?? $"база №{token.DbId} (не открыта)";

                text.AppendLine($"  «{Text(token.Name)}»  →  {Text(database)}"
                              + (token.Enabled ? "" : "  [выключен]"));

                text.AppendLine(token.Used is { } used
                    ? $"    последнее обращение {Moment(used)}"
                    : "    обращений не было");
            }
        }

        text.AppendLine();
        text.AppendLine("  Что позволяет токен: класть записи в свою базу. Читать данные через");
        text.AppendLine("  этот канал нельзя — обработчика, отдающего записи, в программе нет,");
        text.AppendLine("  и наружу отдается только состав коллекций: имена и типы, без значений.");
        text.AppendLine("  Каждый токен — обычный пользователь базы; права ему выдает и отзывает");
        text.AppendLine("  ее администратор той же панелью, что и человеку.");
        text.AppendLine();
    }

    // ── 4. Направления ────────────────────────────────────────────────────────

    private static void Directions(StringBuilder text, IntegrationsRegistry registry,
                                   DateTimeOffset now)
    {
        Title(text, "4. КОМУ РАЗРЕШЕНО УНОСИТЬ ДАННЫЕ");

        var outward = registry.Requests().Where(r => r.Outward).ToList();

        if (outward.Count == 0)
        {
            text.AppendLine("  Запросов, уносящих данные наружу, не заведено ни одного.");
            text.AppendLine();
            return;
        }

        foreach (var request in outward)
        {
            var direction = registry.Direction(request.Id);
            var trouble   = registry.OutwardTrouble(request, now);

            text.AppendLine($"  «{request.Name}»  {request.Method.ToString().ToUpperInvariant()} {request.Path}");

            if (direction is null)
            {
                text.AppendLine("    НЕ ПОДТВЕРЖДЕНО — отправлять не может");
                continue;
            }

            text.AppendLine($"    подтвердил      {Text(direction.Confirmed)}");
            text.AppendLine($"    подтверждено    {Moment(direction.At)}");
            text.AppendLine($"    действует до    {Moment(direction.Until)}");
            text.AppendLine($"    пользовались    {(direction.UsedAt is { } used ? Moment(used) : "ни разу")}");
            text.AppendLine($"    сейчас          {(trouble is null ? "действует" : "НЕ ДЕЙСТВУЕТ: " + trouble)}");
        }

        text.AppendLine();
        text.AppendLine("  Подтверждение выдается мастер-паролем организации, на срок не больше");
        text.AppendLine($"  года, и отключается само после {OutgoingDirection.IdleDays} дней простоя. Любая правка запроса");
        text.AppendLine("  сбрасывает его: направление помнит отпечаток метода, пути, заголовков");
        text.AppendLine("  и тела.");
        text.AppendLine();
    }

    // ── 4. Что уходило ────────────────────────────────────────────────────────

    /// <summary>Сводка по одному направлению за период.</summary>
    private sealed class Flow
    {
        public int           Allowed;
        public int           Blocked;
        public long          Bytes;
        public long          Rows;
        public HashSet<string> Fields = new(StringComparer.OrdinalIgnoreCase);
    }

    private static void Traffic(StringBuilder text, SecurityJournal? journal, DateTimeOffset since)
    {
        Title(text, "5. ЧТО УХОДИЛО ЗА ПЕРИОД");

        if (journal is null)
        {
            text.AppendLine("  Журнал безопасности не открылся — сводку взять неоткуда.");
            text.AppendLine("  Исходящий обмен при этом не работает: отправка без следа не");
            text.AppendLine("  допускается.");
            text.AppendLine();
            return;
        }

        var flows = new Dictionary<string, Flow>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in journal.Stream(new SecurityFilter(SecurityEvent.Send, Since: since)))
        {
            if (!flows.TryGetValue(Where(entry.Address), out var flow))
                flows[Where(entry.Address)] = flow = new Flow();

            if (entry.Allowed) flow.Allowed++; else flow.Blocked++;

            flow.Bytes += entry.Bytes;
            flow.Rows  += entry.Rows;

            foreach (var field in entry.Fields.Split(',', StringSplitOptions.RemoveEmptyEntries
                                                       | StringSplitOptions.TrimEntries))
                flow.Fields.Add(field);
        }

        if (flows.Count == 0)
        {
            text.AppendLine("  За период наружу не уходило ничего: ни одной попытки отправки.");
            text.AppendLine();
            return;
        }

        text.AppendLine($"  Всего попыток отправки: {flows.Values.Sum(f => f.Allowed + f.Blocked):N0}"
                      + $", из них заблокировано {flows.Values.Sum(f => f.Blocked):N0}.");
        text.AppendLine();

        foreach (var (where, flow) in flows.OrderByDescending(p => p.Value.Bytes))
        {
            text.AppendLine($"  {where}");
            text.AppendLine($"    разрешено   {flow.Allowed:N0}"
                          + (flow.Blocked > 0 ? $"   заблокировано {flow.Blocked:N0}" : ""));
            text.AppendLine($"    объем       {Size(flow.Bytes)}   записей {flow.Rows:N0}");
            text.AppendLine($"    поля        {Fields(flow.Fields)}");
        }

        text.AppendLine();
        text.AppendLine("  Адреса сведены без параметров запроса: один и тот же обмен подставляет");
        text.AppendLine("  в них дату и номер страницы, и по полному адресу он рассыпался бы на");
        text.AppendLine("  десятки строк. Названы имена полей; значений в журнале нет.");
        text.AppendLine("  Объем — оба тела разом: и отправленное, и пришедшее в ответ.");
        text.AppendLine();
    }

    // ── 5. Метки ──────────────────────────────────────────────────────────────

    private static void Labels(StringBuilder text, IReadOnlyList<PassportDatabase> databases)
    {
        Title(text, "6. МЕТКИ КОНФИДЕНЦИАЛЬНОСТИ");

        if (databases.Count == 0)
        {
            text.AppendLine("  В момент составления не было открыто ни одной базы данных.");
            text.AppendLine();
            return;
        }

        foreach (var database in databases)
            text.AppendLine($"  «{database.Name}»  {Confidentiality.Title(database.Label)}"
                          + $"   свойств с метками: {database.LabeledFields:N0}");

        text.AppendLine();
        text.AppendLine("  Метка лежит ВНУТРИ файла базы и уезжает вместе с ним: копия базы на");
        text.AppendLine("  соседнем компьютере остается с той же меткой. Сведения только об");
        text.AppendLine("  открытых базах — о закрытых программа в этот момент ничего не знает.");
        text.AppendLine();
    }

    // ── 6. Журнал ─────────────────────────────────────────────────────────────

    private static void Journal(StringBuilder text, SecurityJournal? journal)
    {
        Title(text, "7. ЖУРНАЛ БЕЗОПАСНОСТИ");

        if (journal is null)
        {
            text.AppendLine("  НЕ ОТКРЫЛСЯ. Исходящий обмен на этой машине не работает:");
            text.AppendLine("  отправка, о которой не осталось следа, не допускается.");
            text.AppendLine();
            return;
        }

        var broken = journal.Verify();

        text.AppendLine($"  Файл     {journal.Path}");
        text.AppendLine($"  Строк    {journal.Count():N0}");
        text.AppendLine($"  Цепочка  {(broken == 0 ? "цела: все строки сходятся" : $"РАЗОШЛАСЬ на строке №{broken} — файл правили мимо программы")}");
        text.AppendLine();
    }

    // ── 7. Чего не доказывает ─────────────────────────────────────────────────

    private static void Limits(StringBuilder text, SecurityJournal? journal, int days)
    {
        Title(text, "8. ЧЕГО ЭТОТ ДОКУМЕНТ НЕ ДОКАЗЫВАЕТ");

        text.AppendLine("  Он составлен той самой программой, о которой рассказывает, и потому");
        text.AppendLine("  является сводкой, а не доказательством. Проверяется он журналом");
        text.AppendLine("  безопасности"
                      + (journal is null ? " — сейчас он недоступен." : $": {journal.Path}."));
        text.AppendLine();
        text.AppendLine("  Цепочка отпечатков ловит правку строки и вырезанную строку, но НЕ");
        text.AppendLine("  ловит удаление или подмену файла целиком: пересчитать цепочку заново");
        text.AppendLine("  можно, зная правило. От этого спасала бы копия журнала вне машины —");
        text.AppendLine("  в папке аудита организации. ТАКОЙ КОПИИ ПРОГРАММА НЕ ВЕДЕТ: журнал");
        text.AppendLine("  лежит в одном экземпляре на этом компьютере, и вынос его копии");
        text.AppendLine("  наружу — задача организации, а не программы. Пока копии нет, файл");
        text.AppendLine("  защищен ровно так же, как любой другой файл на этой машине: правами");
        text.AppendLine("  на папку и шифрованием диска.");
        text.AppendLine();
        text.AppendLine($"  Сводка раздела 4 взята за {days} дней. События старше в нее не вошли —");
        text.AppendLine("  они остались в журнале.");
        text.AppendLine();
        text.AppendLine("  Метод запроса отделяет чтение от отправки, но данные можно вписать");
        text.AppendLine("  руками и в адрес читающего запроса. От этого защищает не подтверждение");
        text.AppendLine("  направления, а белый список и журнал, и делать вид, что метод");
        text.AppendLine("  закрывает и этот случай, нельзя.");
        text.AppendLine();
    }

    // ── Мелочи ────────────────────────────────────────────────────────────────

    private static void Title(StringBuilder text, string title)
    {
        text.AppendLine(title);
        text.AppendLine(Line);
    }

    /// <summary>Адрес без параметров запроса — то, по чему сводятся направления.</summary>
    private static string Where(string address)
    {
        if (address.Length == 0) return "адрес не записан";

        var mark = address.IndexOf('?');
        return mark < 0 ? address : address[..mark];
    }

    private static string Fields(HashSet<string> fields)
    {
        if (fields.Count == 0) return "не названы (тело не разбиралось)";

        var names = fields.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

        return names.Count <= MaxFields
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(MaxFields)) + $" и еще {names.Count - MaxFields}";
    }

    private static string Auth(IntegrationConnection connection) => connection.Auth switch
    {
        ConnectionAuth.Header => $"секрет в заголовке «{connection.AuthName}»",
        ConnectionAuth.Query  => $"секрет в параметре «{connection.AuthName}»",
        ConnectionAuth.Basic  => "имя и пароль",
        _                     => "без входа",
    };

    private static string Mode(EgressMode mode) => mode switch
    {
        EgressMode.Internal => "внутренний контур — только сеть организации",
        EgressMode.External => "внешние узлы из белого списка",
        _                   => "изолированный — исходящих обращений нет",
    };

    private static string Moment(DateTimeOffset moment) =>
        moment.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Только число — там, где час ничего не добавляет: границы периода, сроки.</summary>
    private static string Day(DateTimeOffset moment) =>
        moment.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    private static string Text(string value) =>
        string.IsNullOrWhiteSpace(value) ? "не указан" : value;

    private static string Join(IReadOnlyList<string> values) =>
        values.Count == 0 ? "не заданы" : string.Join(", ", values);

    private static string Size(long bytes) => bytes switch
    {
        < 1024L * 1024        => $"{bytes / 1024.0:N0} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:N1} МБ",
        _                     => $"{bytes / 1024.0 / 1024 / 1024:N2} ГБ",
    };
}
