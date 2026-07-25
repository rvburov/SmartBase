using SmartBase.Database.Repositories;

namespace SmartBase.Database;

/// <summary>
/// Доступ к репозиториям ЧУЖИХ баз для межбазовых связей (тип «relation» с TargetDbId&gt;0). Контрол
/// связи ([RelationSelectBox]) и диалог свойства лежат глубоко в дереве и не видят Workspace —
/// поэтому единая точка-сервис, настраиваемая один раз при старте главного окна.
///
/// Резолв «из реестра»: если целевая БД не смонтирована, монтируем её из реестра (приложение и так
/// монтирует все базы реестра в фоне) — названия целей появляются, даже если базы ещё нет в сайдбаре.
/// null — базы нет в реестре или монтирование сорвалось (UI покажет «(запись удалена)»/заглушку).
/// </summary>
public static class RelationDbResolver
{
    private static Workspace? _ws;

    public static void Configure(Workspace ws) => _ws = ws;

    /// <summary>Репозиторий базы по её реестровому DbId. dbId ≤ 0 — вызывающий использует свой
    /// (внутрибазовая связь). Не смонтирована → монтируем из реестра. null — недоступна.</summary>
    public static ConstructorRepository? RepoFor(int dbId)
    {
        if (_ws is null || dbId <= 0) return null;
        var mgr = _ws.Get(dbId);
        if (mgr is null)
        {
            var entry = _ws.Registry.GetAll().FirstOrDefault(d => d.Id == dbId);
            if (entry is not null) mgr = _ws.Mount(entry);
        }
        return mgr?.Constructor;
    }

    /// <summary>Имя базы по DbId (для заглушек «Загрузите базу…»). null — нет в реестре.</summary>
    public static string? DbName(int dbId) =>
        _ws?.Registry.GetAll().FirstOrDefault(d => d.Id == dbId)?.Name;

    /// <summary>Список баз реестра (DbId + имя) — для выбора целевой БД в диалоге свойства-связи.</summary>
    public static IReadOnlyList<(int DbId, string Name)> Databases() =>
        _ws?.Registry.GetAll()
            .Select(d => (d.Id, Name: string.IsNullOrWhiteSpace(d.Name) ? "База данных" : d.Name))
            .ToList()
        ?? [];
}
