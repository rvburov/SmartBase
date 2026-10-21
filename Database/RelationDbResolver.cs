using System.Collections.Concurrent;
using System.Globalization;
using SmartBase.Database.Repositories;

namespace SmartBase.Database;

/// <summary>
/// Доступ к репозиториям ЧУЖИХ баз для межбазовых связей (тип «relation» с TargetDbId&gt;0). Контрол
/// связи ([RelationSelectBox]) и диалог свойства лежат глубоко в дереве и не видят Workspace —
/// поэтому единая точка-сервис, настраиваемая один раз при старте главного окна.
///
/// Связь адресует базу ПОСТОЯННЫМ НОМЕРОМ из самой базы (<see cref="ConstructorRepository.RelationBaseId"/>,
/// от <see cref="MinLinkId"/>), а не номером реестра app.db: номер реестра — счетчик одной установки,
/// и у коллеги, открывшего ту же общую базу, означал бы другую базу. Номера реестра (расширения,
/// последняя открытая запись) — у методов с приставкой Registry.
///
/// Резолв «из реестра»: если целевая БД не смонтирована, но ее номер на этом компьютере уже
/// встречался, монтируем ее запись реестра (приложение и так монтирует все базы реестра в фоне).
/// null — номер неизвестен или монтирование сорвалось (UI покажет «(база недоступна)»).
/// </summary>
public static class RelationDbResolver
{
    /// <summary>Нижняя граница номеров баз для связей. Номера реестра меньше, поэтому старое
    /// ребро с номером реестра никогда не примется за новую базу (см. RelationBaseMigration).</summary>
    public const int MinLinkId = 1_000_000;

    /// <summary>Ключ app_settings: известные этому компьютеру пары «номер реестра → номер базы».</summary>
    internal const string KnownBasesKey = "relation.bases";

    private static Workspace? _ws;
    [ThreadStatic] private static Func<int, ConstructorRepository?>? _scoped;
    [ThreadStatic] private static Func<ConstructorRepository, int>? _scopedIdentity;
    internal static bool HasScopedResolver => _scoped is not null;

    // Номер базы → запись реестра. Монтирование идет в фоне, читает UI — словарь потокобезопасный.
    private static readonly ConcurrentDictionary<int, int> RegistryByLink = new();

    public static bool IsLinkId(int id) => id >= MinLinkId;

    /// <summary>Подключить рабочее пространство окна (null — отключить, стенды и тесты).</summary>
    public static void Configure(Workspace? ws)
    {
        _ws = ws;
        RegistryByLink.Clear();
        if (ws is null) return;
        foreach (var (registryId, linkId) in ParseKnownBases(ws.Registry.Get(KnownBasesKey))) RegistryByLink[linkId] = registryId;
        foreach (var manager in ws.Mounted) Remember(manager);
    }

    /// <summary>Подключенная база сообщает свой номер. Совпавший номер у ДРУГОЙ записи реестра
    /// (файл базы скопировали) не перебивает прежнюю: связи из других баз ведут в оригинал.</summary>
    public static void Remember(DatabaseManager manager)
    {
        var linkId = manager.Constructor.RelationBaseId;
        if (IsLinkId(linkId)) RegistryByLink.TryAdd(linkId, manager.DbId);
    }

    /// <summary>Сохранить известные номера в app_settings. Пары удаленных из реестра баз
    /// отбрасываются, их номер достается подключенной сейчас записи. Только с UI-потока:
    /// контекст реестра общий.</summary>
    public static void SaveKnownBases()
    {
        if (_ws is null) return;
        var registered = _ws.Registry.GetAll().Select(d => d.Id).ToHashSet();
        foreach (var (linkId, registryId) in RegistryByLink.ToArray())
            if (!registered.Contains(registryId)) RegistryByLink.TryRemove(linkId, out _);
        foreach (var manager in _ws.Mounted) Remember(manager);
        var value = string.Join(";", RegistryByLink.OrderBy(p => p.Value).Select(p =>
            $"{p.Value.ToString(CultureInfo.InvariantCulture)}:{p.Key.ToString(CultureInfo.InvariantCulture)}"));
        if (_ws.Registry.Get(KnownBasesKey) != value) _ws.Registry.Set(KnownBasesKey, value);
    }

    internal static IEnumerable<(int RegistryId, int LinkId)> ParseKnownBases(string? value)
    {
        foreach (var pair in (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(':');
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var registryId) && registryId > 0
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var linkId) && IsLinkId(linkId))
                yield return (registryId, linkId);
        }
    }

    /// <summary>Номера, уже занятые известными этому компьютеру базами.</summary>
    internal static IReadOnlyCollection<int> KnownLinkIds() => RegistryByLink.Keys.ToArray();

    /// <summary>Номер базы репозитория для связей; 0 — у базы нет номера.</summary>
    public static int DbIdFor(ConstructorRepository repo)
    {
        if (_scopedIdentity is not null) return _scopedIdentity(repo);
        return repo.RelationBaseId;
    }

    /// <summary>Фоновое вычисление читает только свои подключения; вложенные свойства используют тот же резолвер.</summary>
    public static IDisposable UseScopedResolver(Func<int, ConstructorRepository?> resolver,
        Func<ConstructorRepository, int>? identity = null)
    {
        var previous = _scoped;
        var previousIdentity = _scopedIdentity;
        _scoped = resolver;
        _scopedIdentity = identity;
        return new ResolverScope(previous, previousIdentity);
    }

    private sealed class ResolverScope(Func<int, ConstructorRepository?>? previous,
        Func<ConstructorRepository, int>? identity) : IDisposable
    {
        public void Dispose() { _scoped = previous; _scopedIdentity = identity; }
    }

    /// <summary>Репозиторий базы по номеру связи. dbId ≤ 0 — вызывающий использует свой
    /// (внутрибазовая связь). Не смонтирована → монтируем из реестра. null — недоступна.</summary>
    public static ConstructorRepository? RepoFor(int dbId)
    {
        if (_scoped is not null) return dbId > 0 ? _scoped(dbId) : null;
        if (_ws is null || !IsLinkId(dbId)) return null;
        if (MountedManagerFor(dbId) is { } mounted) return mounted.Constructor;
        if (!RegistryByLink.TryGetValue(dbId, out var registryId)) return null;
        var entry = _ws.Registry.GetAll().FirstOrDefault(d => d.Id == registryId);
        var manager = entry is null ? null : _ws.Mount(entry);
        if (manager is null) return null;
        Remember(manager);
        // Файл по старому пути могли подменить другой базой: номер обязан совпасть.
        return manager.Constructor.RelationBaseId == dbId ? manager.Constructor : null;
    }

    /// <summary>Только уже открытое подключение, без синхронного монтирования.</summary>
    public static ConstructorRepository? MountedRepoFor(int dbId) =>
        dbId <= 0 ? null : _scoped is not null ? _scoped(dbId) : MountedManagerFor(dbId)?.Constructor;

    // Две подключенные базы с одним номером — копия файла. Берем ту, что раньше встала в реестр
    // этого компьютера с этим номером, иначе — с меньшим номером реестра: выбор не скачет.
    private static DatabaseManager? MountedManagerFor(int linkId)
    {
        if (_ws is null) return null;
        var matches = _ws.Mounted.Where(m => m.Constructor.RelationBaseId == linkId).ToList();
        if (matches.Count <= 1) return matches.FirstOrDefault();
        return RegistryByLink.TryGetValue(linkId, out var registryId) && matches.FirstOrDefault(m => m.DbId == registryId) is { } known
            ? known : matches.MinBy(m => m.DbId);
    }

    /// <summary>Базы реестра, у которых известен номер связи (номер + имя), — для выбора целевой
    /// базы в окне свойства-связи.</summary>
    public static IReadOnlyList<(int DbId, string Name)> Databases()
    {
        if (_ws is null) return [];
        var list = new List<(int, string)>();
        foreach (var entry in _ws.Registry.GetAll())
        {
            var linkId = _ws.Get(entry.Id)?.Constructor.RelationBaseId
                         ?? RegistryByLink.FirstOrDefault(p => p.Value == entry.Id).Key;
            if (!IsLinkId(linkId) || list.Any(d => d.Item1 == linkId)) continue;
            list.Add((linkId, string.IsNullOrWhiteSpace(entry.Name) ? "База данных" : entry.Name));
        }
        return list;
    }

    /// <summary>Имя подключенной базы по номеру связи (для текстов окон); null — не подключена.</summary>
    public static string? DbName(int linkId) => MountedManagerFor(linkId)?.Name;

    /// <summary>Подключенная база репозитория и ее строка реестра (проверка связей из окна
    /// свойства: резервной копии нужны оба). null — окно открыто без рабочего пространства.</summary>
    public static (DatabaseManager Manager, Entities.DatabaseRefEntity Entry, IReadOnlyList<PermissionService> Permissions)?
        MountedEntryFor(ConstructorRepository repo)
    {
        if (_ws is null) return null;
        var manager = _ws.Mounted.FirstOrDefault(m => m.Constructor == repo || m.Context.DbPath == repo.DbPath);
        var entry = manager is null ? null : _ws.Registry.GetAll().FirstOrDefault(e => e.Id == manager.DbId);
        return manager is null || entry is null ? null
            : (manager, entry, _ws.Mounted.Select(m => m.Permissions).Distinct().ToArray());
    }

    /// <summary>Номер реестра этого компьютера для номера связи; 0 — база не встречалась.</summary>
    public static int RegistryIdFor(int linkId) =>
        MountedManagerFor(linkId)?.DbId ?? (RegistryByLink.TryGetValue(linkId, out var id) ? id : 0);

    /// <summary>Номер реестра базы репозитория (журнал безопасности, обмен, последняя запись).</summary>
    public static int RegistryIdFor(ConstructorRepository repo) =>
        _ws?.Mounted.FirstOrDefault(m => m.Constructor == repo || m.Context.DbPath == repo.DbPath)?.DbId
        ?? repo.Permissions?.DbId ?? 0;

    /// <summary>Список баз реестра (номер реестра + имя) — для расширений и окон, которые
    /// выбирают базу этого компьютера, а не цель связи.</summary>
    public static IReadOnlyList<(int DbId, string Name)> RegistryDatabases() =>
        _ws?.Registry.GetAll()
            .Select(d => (d.Id, Name: string.IsNullOrWhiteSpace(d.Name) ? "База данных" : d.Name))
            .ToList()
        ?? [];

    /// <summary>Репозиторий базы по номеру реестра. Не смонтирована → монтируем из реестра.</summary>
    public static ConstructorRepository? RegistryRepoFor(int registryId)
    {
        if (_ws is null || registryId <= 0) return null;
        var mgr = _ws.Get(registryId);
        if (mgr is null)
        {
            var entry = _ws.Registry.GetAll().FirstOrDefault(d => d.Id == registryId);
            if (entry is not null) mgr = _ws.Mount(entry);
            if (mgr is not null) Remember(mgr);
        }
        return mgr?.Constructor;
    }

    /// <summary>Уже СМОНТИРОВАННЫЕ базы (номер связи + репозиторий), кроме <paramref name="excludeDbId"/>.
    /// Ничего не монтирует (в отличие от <see cref="RepoFor"/>) — для best-effort подсчета межбазовых
    /// ВХОДЯЩИХ связей перед удалением: закрытые базы не считаем, их ребра вычистятся лениво при
    /// открытии. Репозитории общие → звать только с UI-потока.</summary>
    public static IReadOnlyList<(int DbId, ConstructorRepository Repo)> MountedReposExcept(int excludeDbId)
    {
        if (_ws is null) return [];
        var list = new List<(int, ConstructorRepository)>();
        foreach (var manager in _ws.Mounted.OrderBy(m => m.DbId))
        {
            var linkId = manager.Constructor.RelationBaseId;
            if (!IsLinkId(linkId) || linkId == excludeDbId || list.Any(p => p.Item1 == linkId)) continue;
            if (MountedManagerFor(linkId) == manager) list.Add((linkId, manager.Constructor));
        }
        return list;
    }
}
