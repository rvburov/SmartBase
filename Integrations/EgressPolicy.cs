using System.Net;

namespace SmartBase.Integrations;

/// <summary>
/// Режим контура — насколько далеко программе позволено обращаться наружу. Порядок значений
/// значим: чем меньше число, тем строже режим, и на этом держится выбор строжайшего при
/// пересечении политик (<see cref="EgressPolicySet"/>).
/// </summary>
public enum EgressMode
{
    /// <summary>Исходящих нет вовсе. Состояние по умолчанию и единственное, которое
    /// получается, когда политики нет или она не прочиталась.</summary>
    Isolated = 0,

    /// <summary>Только адреса организации: строка обязана быть в белом списке И адрес,
    /// в который разрешилось имя, обязан быть приватным.</summary>
    Internal = 1,

    /// <summary>Поименные внешние узлы из белого списка. Приватность адреса не требуется.</summary>
    External = 2,
}

/// <summary>
/// Политика организации из одного источника: что этот источник разрешает. Пользователь ее не
/// правит — файл приезжает извне программы (реестр, папка установки, папка раздачи), см.
/// docs/ROADMAP-API.md, раздел 10.3.
///
/// Объект неизменяемый и без ввода-вывода: чтение источников, проверка подписи и кеш последней
/// известной политики — забота загрузчика, здесь только решение «разрешено или нет».
/// </summary>
/// <param name="Mode">Режим контура этого источника.</param>
/// <param name="Hosts">Маски имен: <c>api.zavod.local</c>, <c>*.zavod.local</c>.</param>
/// <param name="Networks">Сети: <c>10.0.0.0/8</c>; голый адрес — сеть из одного адреса.</param>
/// <param name="Folders">Корни папок, внутри которых сценариям разрешено читать и писать.</param>
/// <param name="MaxRowsPerRun">Потолок строк за один запуск; 0 — отправлять нечего.</param>
/// <param name="MaxBytesPerDay">Потолок байт в сутки на направление; 0 — отправлять нечего.</param>
/// <param name="MaxRunsPerHour">Потолок запусков в час; 0 — запускать нечего.</param>
/// <param name="ValidUntil">Последний день действия включительно; <c>null</c> — без срока.</param>
/// <param name="UserMayAddHosts">Разрешено ли пользователю дописывать адреса самому.</param>
/// <param name="Source">Откуда политика прочитана — для экрана «Безопасность» и журнала.</param>
/// <param name="MasterHash">Проверочный хэш мастер-пароля (<see cref="MasterPassword"/>); пусто —
/// этот источник подтверждать исходящие направления не позволяет вовсе.</param>
/// <param name="Mirror">Куда складывать зеркало исходящего — копию всего, что уходит наружу
/// (docs/ROADMAP-API.md, 10.1). Пусто — зеркала нет; это состояние по умолчанию. Папку называет
/// ОРГАНИЗАЦИЯ, и не случайно: в отличие от журнала безопасности, зеркало содержит сами данные,
/// и лежать им место там, куда доступ есть у проверяющего, а не у того, за кем ведется
/// наблюдение.</param>
/// <param name="AllowListen">Разрешает ли этот источник слушать АДРЕС МАШИНЫ в локальной сети
/// (docs/ROADMAP-API.md, 10.12 и шаг 8). Петлевой адрес сюда не относится: к <c>127.0.0.1</c>
/// может обратиться только программа этой же машины, и разрешать ей то, что она и так может,
/// незачем. По умолчанию <c>false</c> — сетевой режим включается точечно, на машине обмена.</param>
public sealed record EgressPolicy(
    EgressMode              Mode,
    IReadOnlyList<string>   Hosts,
    IReadOnlyList<string>   Networks,
    IReadOnlyList<string>   Folders,
    int                     MaxRowsPerRun,
    long                    MaxBytesPerDay,
    int                     MaxRunsPerHour,
    DateOnly?               ValidUntil,
    bool                    UserMayAddHosts,
    string                  Source,
    string                  MasterHash = "",
    bool                    AllowListen = false,
    string                  Mirror      = "")
{
    /// <summary>
    /// Самая строгая политика: наружу нельзя ничего. Ею подменяется любой источник, который
    /// не прочитался, — отсутствие настройки обязано быть безопасным состоянием, иначе новая
    /// машина открыта ровно до тех пор, пока про нее не вспомнят.
    /// </summary>
    public static EgressPolicy Closed { get; } = new(
        EgressMode.Isolated, [], [], [], 0, 0, 0, null, false, "нет политики");

    /// <summary>Позволяет ли ЭТОТ источник подтверждать исходящие направления.</summary>
    public bool HasMaster => MasterPassword.Defined(MasterHash);

    /// <summary>Разрешает ли ЭТОТ источник имя узла (без учета режима и срока).</summary>
    public bool AllowsHost(string? host) =>
        Hosts.Any(mask => NetworkAddress.HostMatches(host, mask));

    /// <summary>Разрешает ли ЭТОТ источник фактический адрес соединения.</summary>
    public bool AllowsAddress(IPAddress? address) =>
        Networks.Any(net => NetworkAddress.InNetwork(address, net));

    /// <summary>
    /// Лежит ли путь внутри разрешенного корня. Сравнение идет по полному пути, поэтому
    /// «..» в середине не выводит за корень, а соседняя папка с похожим именем
    /// (<c>Обмен2</c> при корне <c>Обмен</c>) не считается вложенной — для этого и требуется
    /// разделитель после корня.
    ///
    /// Регистр учитывается по правилам файловой системы: на Windows пути регистронезависимы,
    /// на Linux <c>/data</c> и <c>/Data</c> — разные папки, и игнорировать там регистр значило
    /// бы разрешать путь, которого в корне нет.
    /// </summary>
    public bool AllowsFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var compare = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return Folders.Any(root =>
        {
            if (string.IsNullOrWhiteSpace(root)) return false;
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full.Equals(prefix, compare)
                || full.StartsWith(prefix + Path.DirectorySeparatorChar, compare);
        });
    }
}

/// <summary>
/// Все найденные источники политики разом. Правило одно: **разрешено то, что разрешают ВСЕ**
/// источники, — это и есть «применяется пересечение» из docs/ROADMAP-API.md (10.3). Считать
/// пересечение списков масок не нужно и вредно: маски пересекаются неочевидно, а спросить
/// каждый источник — операция точная и проверяемая.
///
/// Пустой набор — не «ограничений нет», а «ничего нельзя»: без этого случай «политику не
/// нашли» открывал бы больше, чем случай «политика нашлась и все запрещает».
/// </summary>
public sealed class EgressPolicySet
{
    /// <summary>Набор без источников: наружу нельзя ничего.</summary>
    public static EgressPolicySet Closed { get; } = new([]);

    public EgressPolicySet(IReadOnlyList<EgressPolicy> sources) => Sources = sources;

    /// <summary>Набор из перечисленных источников; ни одного — получится <see cref="Closed"/>.</summary>
    public static EgressPolicySet Of(params EgressPolicy[] sources) => new(sources);

    /// <summary>Источники в том порядке, в каком их нашел загрузчик.</summary>
    public IReadOnlyList<EgressPolicy> Sources { get; }

    /// <summary>Есть ли хоть одна прочитанная политика.</summary>
    public bool Any => Sources.Count > 0;

    /// <summary>Строжайший режим из всех источников; без источников — изолированный.</summary>
    public EgressMode Mode =>
        Sources.Count == 0 ? EgressMode.Isolated : Sources.Min(p => p.Mode);

    /// <summary>Наименьший потолок строк за запуск; без источников — ноль.</summary>
    public int MaxRowsPerRun =>
        Sources.Count == 0 ? 0 : Sources.Min(p => p.MaxRowsPerRun);

    /// <summary>Наименьший потолок байт в сутки; без источников — ноль.</summary>
    public long MaxBytesPerDay =>
        Sources.Count == 0 ? 0 : Sources.Min(p => p.MaxBytesPerDay);

    /// <summary>Наименьший потолок запусков в час; без источников — ноль.</summary>
    public int MaxRunsPerHour =>
        Sources.Count == 0 ? 0 : Sources.Min(p => p.MaxRunsPerHour);

    /// <summary>Ближайшая дата окончания среди источников; <c>null</c> — ни один не ограничен сроком.</summary>
    public DateOnly? ValidUntil
    {
        get
        {
            DateOnly? nearest = null;
            foreach (var until in Sources.Select(p => p.ValidUntil).OfType<DateOnly>())
                if (nearest is null || until < nearest) nearest = until;
            return nearest;
        }
    }

    /// <summary>Истек ли срок хоть одного источника на указанный день.</summary>
    public bool Expired(DateOnly today) => ValidUntil is { } until && today > until;

    /// <summary>Дописывать адреса самому разрешено, только если это позволяют все источники.</summary>
    public bool UserMayAddHosts =>
        Sources.Count > 0 && Sources.All(p => p.UserMayAddHosts);

    /// <summary>
    /// Слушать адрес машины в сети разрешено, только если это позволяет КАЖДЫЙ источник. Правило
    /// то же, что у остальных рамок, и здесь оно особенно уместно: открытый в сеть порт — то, о
    /// чем спрашивают в первую очередь, и «хоть один разрешил» было бы неверным ответом.
    /// </summary>
    public bool AllowsListen =>
        Sources.Count > 0 && Sources.All(p => p.AllowListen);

    /// <summary>
    /// Куда складывать зеркало исходящего. Здесь пересечения НЕТ, и это не недосмотр: зеркало —
    /// не разрешение, а указание, и два источника, назвавшие разные папки, означают две
    /// инстанции, каждая из которых хочет свою копию. Пишем в обе.
    /// </summary>
    public IReadOnlyList<string> Mirrors =>
        [.. Sources.Select(p => p.Mirror)
                   .Where(m => !string.IsNullOrWhiteSpace(m))
                   .Select(m => m.Trim())
                   .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Имя узла разрешено, только если его разрешает каждый источник.</summary>
    public bool AllowsHost(string? host) =>
        Sources.Count > 0 && Sources.All(p => p.AllowsHost(host));

    /// <summary>Фактический адрес соединения разрешен, только если его разрешает каждый источник.</summary>
    public bool AllowsAddress(IPAddress? address) =>
        Sources.Count > 0 && Sources.All(p => p.AllowsAddress(address));

    /// <summary>Путь разрешен, только если он лежит внутри разрешенных корней каждого источника.</summary>
    public bool AllowsFolder(string? path) =>
        Sources.Count > 0 && Sources.All(p => p.AllowsFolder(path));

    /// <summary>
    /// Есть ли чем подтверждать исходящие направления — хотя бы один источник назвал
    /// мастер-пароль.
    /// </summary>
    public bool HasMaster => Sources.Any(p => p.HasMaster);

    /// <summary>
    /// Подходит ли пароль. Правило то же, что у остальных рамок, — ПЕРЕСЕЧЕНИЕ: пароль обязан
    /// подойти КАЖДОМУ источнику, который его назвал.
    ///
    /// Два источника с разными паролями означают, что две инстанции не договорились между собой,
    /// и подтвердить направление нельзя ни одним из них. Это не придирка: отдать полномочие по
    /// любому из двух паролей значило бы, что более строгий источник обходится наличием более
    /// мягкого — ровно то, ради чего пересечение и заведено.
    ///
    /// Ни один источник пароля не назвал — <c>false</c>. Подтверждать нечем, а значит, исходящих
    /// направлений нет: отсутствие настройки обязано быть безопасным состоянием.
    /// </summary>
    public bool MasterMatches(string? password) =>
        HasMaster && Sources.Where(p => p.HasMaster)
                            .All(p => MasterPassword.Matches(p.MasterHash, password));
}
