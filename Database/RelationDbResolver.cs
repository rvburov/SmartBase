using SmartBase.Database.Repositories;

namespace SmartBase.Database;

/// <summary>
/// Доступ к репозиториям ЧУЖИХ баз для межбазовых связей (тип «relation» с TargetDbId&gt;0). Контрол
/// связи ([RelationSelectBox]) и диалог свойства лежат глубоко в дереве и не видят Workspace —
/// поэтому единая точка-сервис, настраиваемая один раз при старте главного окна.
///
/// Резолв «из реестра»: если целевая БД не смонтирована, монтируем ее из реестра (приложение и так
/// монтирует все базы реестра в фоне) — названия целей появляются, даже если базы еще нет в сайдбаре.
/// null — базы нет в реестре или монтирование сорвалось (UI покажет «(база недоступна)»).
/// </summary>
public static class RelationDbResolver
{
    private static Workspace? _ws;
    [ThreadStatic] private static Func<int, ConstructorRepository?>? _scoped;
    [ThreadStatic] private static Func<ConstructorRepository, int>? _scopedIdentity;
    internal static bool HasScopedResolver => _scoped is not null;

    public static void Configure(Workspace ws) => _ws = ws;

    /// <summary>Абсолютная идентичность базы репозитория; 0 — неизвестна у изолированного клона.</summary>
    public static int DbIdFor(ConstructorRepository repo)
    {
        if (_scopedIdentity is not null) return _scopedIdentity(repo);
        if (repo.Permissions is { DbId: > 0 } permissions) return permissions.DbId;
        return _ws?.Mounted.FirstOrDefault(m => m.Constructor == repo || m.Context.DbPath == repo.DbPath)?.DbId ?? 0;
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

    /// <summary>Репозиторий базы по ее реестровому DbId. dbId ≤ 0 — вызывающий использует свой
    /// (внутрибазовая связь). Не смонтирована → монтируем из реестра. null — недоступна.</summary>
    public static ConstructorRepository? RepoFor(int dbId)
    {
        if (_scoped is not null) return dbId > 0 ? _scoped(dbId) : null;
        if (_ws is null || dbId <= 0) return null;
        var mgr = _ws.Get(dbId);
        if (mgr is null)
        {
            var entry = _ws.Registry.GetAll().FirstOrDefault(d => d.Id == dbId);
            if (entry is not null) mgr = _ws.Mount(entry);
        }
        return mgr?.Constructor;
    }

    /// <summary>Только уже открытое подключение, без синхронного монтирования.</summary>
    public static ConstructorRepository? MountedRepoFor(int dbId) =>
        dbId <= 0 ? null : _scoped is not null ? _scoped(dbId) : _ws?.Get(dbId)?.Constructor;

    /// <summary>Имя базы по DbId (для заглушек «Загрузите базу…»). null — нет в реестре.</summary>
    public static string? DbName(int dbId) =>
        _ws?.Registry.GetAll().FirstOrDefault(d => d.Id == dbId)?.Name;

    /// <summary>Список баз реестра (DbId + имя) — для выбора целевой БД в диалоге свойства-связи.</summary>
    public static IReadOnlyList<(int DbId, string Name)> Databases() =>
        _ws?.Registry.GetAll()
            .Select(d => (d.Id, Name: string.IsNullOrWhiteSpace(d.Name) ? "База данных" : d.Name))
            .ToList()
        ?? [];

    /// <summary>Уже СМОНТИРОВАННЫЕ базы (DbId + их репозиторий), кроме <paramref name="excludeDbId"/>.
    /// Ничего не монтирует (в отличие от <see cref="RepoFor"/>) — для best-effort подсчета межбазовых
    /// ВХОДЯЩИХ связей перед удалением: закрытые базы не считаем, их ребра вычистятся лениво при
    /// открытии. Репозитории общие → звать только с UI-потока.</summary>
    public static IReadOnlyList<(int DbId, ConstructorRepository Repo)> MountedReposExcept(int excludeDbId)
    {
        if (_ws is null) return [];
        var list = new List<(int, ConstructorRepository)>();
        foreach (var d in _ws.Registry.GetAll())
        {
            if (d.Id == excludeDbId) continue;
            if (_ws.Get(d.Id)?.Constructor is { } repo) list.Add((d.Id, repo));
        }
        return list;
    }
}
