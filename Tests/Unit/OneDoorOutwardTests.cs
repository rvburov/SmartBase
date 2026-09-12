namespace SmartBase.Tests.Unit;

/// <summary>
/// Сторож правила «наружу — одна дверь» (docs/ROADMAP-API.md, раздел 10.2, и
/// docs/developer/features/integrations/overview.md). Смысл правила не в порядке ради порядка:
/// на вопрос проверяющего «где программа выходит наружу» ответ обязан быть «один файл», а не
/// «надо посмотреть весь проект». Стоит завестись второму <c>HttpClient</c> где-нибудь в
/// расширении — и белый список, квоты и журнал мимо него не работают, а узнать об этом
/// получится только чтением всего кода.
///
/// Правило словесное и потому теряется — как и стандарт прокрутки, за которым следит
/// <see cref="ScrollingConventionTests"/>. Поэтому оно проверяется сборкой.
///
/// Тест намеренно грубый: ищет упоминания сетевых типов в тексте файлов, а не разбирает код.
/// Ложное срабатывание на комментарий здесь — не беда, а признак того, что кто-то собрался
/// писать сетевой вызов не в том месте.
/// </summary>
public sealed class OneDoorOutwardTests
{
    /// <summary>Папки с кодом приложения. Tests/ не сканируется: тут сами названия типов и лежат.</summary>
    private static readonly string[] SourceDirs =
    [
        "Controls", "Core", "Database", "Extensions", "Integrations",
        "Logging", "Notifications", "Settings", "Setup", "Utils", "ViewModels", "Views",
    ];

    /// <summary>Обращение к чужой программе по сети или почтой. Разрешено только за шлюзом.</summary>
    private static readonly string[] OutboundTypes =
    [
        "HttpClient", "HttpRequestMessage", "HttpWebRequest",
        "WebRequest", "WebClient", "SmtpClient", "System.Net.Http",
    ];

    /// <summary>Сырые сокеты — уровнем ниже; отдельный список, потому что и исключения другие.</summary>
    private static readonly string[] SocketTypes =
    [
        "System.Net.Sockets", "TcpClient", "new Socket",
    ];

    /// <summary>
    /// Слушать порт. Отдельный список, потому что это ДРУГАЯ дверь: наружу через нее не уходит
    /// ничего, а вот пускать внутрь она может — и решение «кого пускать» обязано быть в одном
    /// месте ровно по той же причине, что и «куда ходить».
    /// </summary>
    private static readonly string[] ListenerTypes =
    [
        "HttpListener", "TcpListener",
    ];

    /// <summary>Единственный файл, которому позволено слушать порт.</summary>
    private static readonly string InwardDoor =
        Path.Combine("Integrations", "IngressHttp.Open.cs");

    /// <summary>
    /// Где сокеты законны и почему. Это НЕ интеграции: PostgreSQL — своя база данных
    /// приложения, а не чужая программа. Проба порта нужна, чтобы отличить «сервер выключен»
    /// от «пароль не тот», а <c>SocketException</c> ловится, чтобы пропажа сети не выглядела
    /// поломкой базы. Список именно поименный: новый сокет в любом другом файле обязан
    /// уронить сборку и заставить объяснить, зачем он там.
    /// </summary>
    private static readonly string[] SocketsAllowedIn =
    [
        Path.Combine("Utils", "PgServerProbe.cs"),
        Path.Combine("Database", "DbHealthMonitor.cs"),
        Path.Combine("Database", "PgErrors.cs"),
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SmartBase.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);   // тест бесполезен, если корень репозитория не найден
        return dir!.FullName;
    }

    private static IEnumerable<(string Path, string Text)> Sources(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.TopDirectoryOnly))
            yield return (Path.GetRelativePath(root, file), File.ReadAllText(file));

        foreach (var dir in SourceDirs)
        {
            var path = Path.Combine(root, dir);
            if (!Directory.Exists(path)) continue;

            foreach (var file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
                yield return (Path.GetRelativePath(root, file), File.ReadAllText(file));
        }
    }

    private static bool InsideGate(string relativePath) =>
        relativePath.StartsWith("Integrations" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>
    /// Обращения к чужим программам — только за шлюзом. Правило действует и на
    /// <c>Extensions/</c>: расширение получает сеть шагом сценария, а не своим клиентом,
    /// иначе политика и журнал размножаются по числу расширений.
    /// </summary>
    [Fact]
    public void Outbound_types_live_only_behind_the_gate()
    {
        var root = RepoRoot();

        var offenders = Sources(root)
            .Where(f => !InsideGate(f.Path))
            .SelectMany(f => OutboundTypes
                .Where(type => f.Text.Contains(type, StringComparison.Ordinal))
                .Select(type => $"{f.Path}: {type}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Обращение наружу мимо шлюза Integrations/EgressGate.cs:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Слушатель живет ровно в одном файле. Правило строже исходящего: у двери наружу разрешена
    /// вся папка <c>Integrations/</c>, а слушателю — один поименный файл, потому что второй
    /// слушатель означал бы второй набор правил о том, кого пускать в базу.
    /// </summary>
    [Fact]
    public void Listener_lives_only_in_the_inward_door()
    {
        var root = RepoRoot();

        var offenders = Sources(root)
            .Where(f => f.Path != InwardDoor)
            .SelectMany(f => ListenerTypes
                .Where(type => f.Text.Contains(type, StringComparison.Ordinal))
                .Select(type => $"{f.Path}: {type}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Слушатель порта вне двери внутрь Integrations/IngressHttp.Open.cs:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>Сама дверь внутрь обязана быть на месте — иначе тест выше вечнозеленый.</summary>
    [Fact]
    public void The_inward_door_itself_is_in_place()
    {
        var root = RepoRoot();

        Assert.True(File.Exists(Path.Combine(root, InwardDoor)));
        Assert.True(File.Exists(Path.Combine(root, "Integrations", "IngressGate.cs")));
    }

    /// <summary>Сырые сокеты — только у шлюза и у поименного списка файлов PostgreSQL.</summary>
    [Fact]
    public void Raw_sockets_live_only_in_the_gate_and_in_the_postgres_files()
    {
        var root = RepoRoot();

        var offenders = Sources(root)
            .Where(f => !InsideGate(f.Path) && !SocketsAllowedIn.Contains(f.Path))
            .SelectMany(f => SocketTypes
                .Where(type => f.Text.Contains(type, StringComparison.Ordinal))
                .Select(type => $"{f.Path}: {type}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Сырой сокет вне шлюза и вне файлов PostgreSQL:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Список исключений не должен превращаться в мусор: файл переименовали или удалили —
    /// строку отсюда надо убрать, иначе исключение живет вечно и уже ничего не значит.
    /// </summary>
    [Fact]
    public void Socket_exceptions_point_at_files_that_exist()
    {
        var root = RepoRoot();

        foreach (var relative in SocketsAllowedIn)
            Assert.True(File.Exists(Path.Combine(root, relative)),
                $"В списке исключений указан несуществующий файл: {relative}");
    }

    /// <summary>
    /// Условная компиляция закрытой сборки живет ровно в одном файле — в шлюзе. Расползись
    /// <c>#if CLOSED_LOOP</c> по коду, и дистрибутивов станет два разных: проверять пришлось бы
    /// оба целиком, а сейчас различие описывается одной фразой — в закрытой сборке шлюз
    /// отвечает отказом и сетевого клиента за ним нет (docs/ROADMAP-API.md, 10.16.1).
    /// </summary>
    [Fact]
    public void Closed_loop_switch_lives_only_in_the_gate()
    {
        var root = RepoRoot();
        var gate = Path.Combine("Integrations", "EgressGate.cs");

        var offenders = Sources(root)
            .Where(f => f.Path != gate && f.Text.Contains("CLOSED_LOOP", StringComparison.Ordinal))
            .Select(f => f.Path)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Условная компиляция закрытой сборки вне шлюза:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Отправляющий слой закрытой сборки — заглушка, и в ней не должно быть ни сетевого
    /// клиента, ни сокета. Иначе обещание «в закрытом дистрибутиве сетевого кода нет»
    /// перестает быть правдой, а заметить это чтением настроек невозможно.
    /// </summary>
    [Fact]
    public void Closed_loop_transport_has_no_network_code()
    {
        var path = Path.Combine(RepoRoot(), "Integrations", "EgressHttp.Closed.cs");
        Assert.True(File.Exists(path), "Заглушка отправляющего слоя пропала: " + path);

        var text = File.ReadAllText(path);

        var found = OutboundTypes.Concat(SocketTypes).Concat(ListenerTypes)
            .Where(type => text.Contains(type, StringComparison.Ordinal))
            .ToList();

        Assert.True(found.Count == 0,
            "В заглушке закрытой сборки завелся сетевой код:\n  " + string.Join("\n  ", found));
    }

    /// <summary>
    /// Подмена отправляющего слоя объявлена в проекте. Без этой проверки можно переименовать
    /// файл или потерять строку в SmartBase.csproj — и закрытая сборка молча соберется с
    /// обычным сетевым клиентом внутри.
    /// </summary>
    [Fact]
    public void Closed_loop_swap_is_declared_in_the_project()
    {
        var project = File.ReadAllText(Path.Combine(RepoRoot(), "SmartBase.csproj"));

        Assert.Contains("'$(ClosedLoop)' == 'true'", project, StringComparison.Ordinal);
        Assert.Contains("<Compile Remove=\"Integrations\\EgressHttp.Open.cs\" />", project,
            StringComparison.Ordinal);
        Assert.Contains("<Compile Remove=\"Integrations\\EgressHttp.Closed.cs\" />", project,
            StringComparison.Ordinal);

        Assert.True(File.Exists(Path.Combine(RepoRoot(), "Integrations", "EgressHttp.Open.cs")));
    }

    /// <summary>
    /// Сам шлюз обязан быть на месте. Без этой проверки удаление файла превратило бы оба
    /// теста выше в вечнозеленые: запрещать станет нечего и некому.
    /// </summary>
    [Fact]
    public void The_gate_itself_is_in_place()
    {
        var root = RepoRoot();

        Assert.True(File.Exists(Path.Combine(root, "Integrations", "EgressGate.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Integrations", "EgressPolicy.cs")));
    }

    // ── Второй рубеж: анализатор запрещенных вызовов ──────────────────────────

    /// <summary>
    /// Строка, которой снимается запрет анализатора. Ищется дословно: пробел внутри — часть
    /// синтаксиса, и вариантов написания у нее нет.
    /// </summary>
    private const string Suppression = "#pragma warning disable RS0030";

    /// <summary>
    /// Анализатор подключен и его список на месте. Тесты этого файла проверяют ТЕКСТ, а
    /// анализатор — типы, и ловят они разное: через <c>var</c>, псевдоним <c>using</c> или
    /// обобщенный параметр имя типа в исходнике не появляется вовсе, и текстовый поиск такой
    /// вызов пропустит. Потеряйся строка из проекта — второй рубеж исчезнет молча, а сборка
    /// продолжит идти: заметить это можно только отсюда.
    /// </summary>
    [Fact]
    public void Banned_api_analyzer_is_wired_into_the_project()
    {
        var root    = RepoRoot();
        var project = File.ReadAllText(Path.Combine(root, "SmartBase.csproj"));

        Assert.Contains("Microsoft.CodeAnalysis.BannedApiAnalyzers", project, StringComparison.Ordinal);
        Assert.Contains("<AdditionalFiles Include=\"BannedSymbols.txt\" />", project, StringComparison.Ordinal);

        // Предупреждение в проекте, где их и так три сотни от чужих пакетов, никто не увидит.
        Assert.Contains("RS0030", project, StringComparison.Ordinal);
        Assert.Contains("<WarningsAsErrors>", project, StringComparison.Ordinal);

        var banned = File.ReadAllText(Path.Combine(root, "BannedSymbols.txt"));

        // Список может пополняться, но эти четыре — то самое, ради чего он заведен.
        foreach (var symbol in new[]
                 {
                     "T:System.Net.Http.HttpClient;",
                     "T:System.Net.Sockets.Socket;",
                     "T:System.Net.HttpListener;",
                     "T:System.Net.Dns;",
                 })
            Assert.Contains(symbol, banned, StringComparison.Ordinal);
    }

    /// <summary>
    /// Снятий запрета ровно три, и все поименные: дверь наружу, дверь внутрь и проба порта
    /// PostgreSQL (своя база приложения, а не чужая программа). Четвертое снятие — это четвертая
    /// дверь, и появиться она должна не молча: правило «дверей по пальцам» держится не самим
    /// анализатором, а тем, что исключений из него считаное число.
    /// </summary>
    [Fact]
    public void Suppressions_live_only_at_the_two_doors_and_the_postgres_probe()
    {
        var root = RepoRoot();

        var allowed = new[]
        {
            Path.Combine("Integrations", "EgressHttp.Open.cs"),
            InwardDoor,
            Path.Combine("Utils", "PgServerProbe.cs"),
        };

        var found = Sources(root)
            .Where(f => f.Text.Contains(Suppression, StringComparison.Ordinal))
            .Select(f => f.Path)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(allowed.OrderBy(p => p, StringComparer.Ordinal), found);
    }

    // ── Посторонние процессы на пути обмена ───────────────────────────────────

    /// <summary>
    /// Путь обмена: шлюз, движок сценариев и прием файлов. Именно здесь исполняется то, что
    /// настроил человек, — и здесь же запуск постороннего процесса означал бы «выполнить
    /// команду с той стороны».
    /// </summary>
    private static bool OnExchangePath(string relativePath)
    {
        if (InsideGate(relativePath)) return true;

        var db = "Database" + Path.DirectorySeparatorChar;
        if (!relativePath.StartsWith(db, StringComparison.Ordinal)) return false;

        var name = Path.GetFileName(relativePath);
        return name.StartsWith("Scenario", StringComparison.Ordinal)
            || name.StartsWith("Inbox", StringComparison.Ordinal)
            || name.StartsWith("Integrations", StringComparison.Ordinal);
    }

    /// <summary>
    /// Обмен не запускает посторонних процессов (10.2). Запрет НЕ на всю программу: открыть
    /// файл или ссылку в системе — обычное дело, этим заняты «Диагностика», расширения и
    /// помощник PostgreSQL. Запрещено ровно то, о чем говорит раздел 13: шаг «выполнить
    /// команду» и любой встроенный интерпретатор. Заведись <c>Process.Start</c> на пути
    /// обмена — и сценарий, приехавший файлом из папки, получил бы исполнение на машине.
    /// </summary>
    [Fact]
    public void The_exchange_path_never_starts_a_foreign_process()
    {
        var root = RepoRoot();

        var offenders = Sources(root)
            .Where(f => OnExchangePath(f.Path))
            .SelectMany(f => new[] { "Process.Start", "ProcessStartInfo" }
                .Where(type => f.Text.Contains(type, StringComparison.Ordinal))
                .Select(type => $"{f.Path}: {type}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Запуск постороннего процесса на пути обмена:\n  " + string.Join("\n  ", offenders));
    }
}
