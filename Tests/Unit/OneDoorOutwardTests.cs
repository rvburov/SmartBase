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
        "HttpClient", "HttpListener", "HttpRequestMessage", "HttpWebRequest",
        "WebRequest", "WebClient", "SmtpClient", "System.Net.Http",
    ];

    /// <summary>Сырые сокеты — уровнем ниже; отдельный список, потому что и исключения другие.</summary>
    private static readonly string[] SocketTypes =
    [
        "System.Net.Sockets", "TcpClient", "TcpListener", "new Socket",
    ];

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

        var found = OutboundTypes.Concat(SocketTypes)
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
}
