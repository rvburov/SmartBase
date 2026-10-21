using SmartBase.Database.Repositories;

namespace SmartBase.Database;

/// <summary>
/// Есть ли ВХОДЯЩИЕ связи (тип «relation»), которые оборвет удаление / перенос записей / коллекции /
/// группы — для окна подтверждения «связи будут удалены» (без числа: нужен лишь факт). Проверяет
/// внутрибазовые (TargetDbId=0, чистятся сразу) и межбазовые из ДРУГИХ ОТКРЫТЫХ баз (обрываются →
/// «связи нет», лениво). Закрытые базы не проверяем (их ребра вычистятся при следующей правке) — best-effort.
/// Any/EXISTS обрывается на первом ребре, поэтому дешево даже на больших базах. Общие репозитории →
/// звать только с UI-потока.
/// </summary>
public static class RelationDeleteImpact
{
    /// <summary>Есть ли входящие связи на записи <paramref name="recordIds"/> (удаление/перенос записи).</summary>
    public static bool ForRecords(ConstructorRepository owner, IReadOnlyCollection<int> recordIds)
    {
        if (recordIds.Count == 0) return false;
        var ownerDbId = RelationDbResolver.DbIdFor(owner);
        if (owner.HasIncomingLinks(recordIds, 0)) return true;                 // внутрибазовые
        // Межбазовые ищем ТОЛЬКО при известном номере нашей базы: чужая база различает ребра «в нашу
        // базу» по TargetDbId == номеру. Номера нет (≤0) — пропускаем, иначе с targetDbId=0 совпали бы
        // ВНУТРЕННИЕ ребра чужой базы по коллизии id (у баз id могут пересекаться).
        if (ownerDbId <= 0) return false;
        foreach (var (_, repo) in RelationDbResolver.MountedReposExcept(ownerDbId))
            if (repo.HasIncomingLinks(recordIds, ownerDbId)) return true;      // межбазовые из открытых баз
        return false;
    }

    /// <summary>Есть ли входящие связи на записи коллекций <paramref name="collectionIds"/>
    /// (удаление коллекции/группы; в т.ч. ветка группы).</summary>
    public static bool ForCollections(ConstructorRepository owner, IReadOnlyCollection<int> collectionIds)
    {
        if (collectionIds.Count == 0) return false;
        if (owner.HasIncomingLinksToCollections(collectionIds)) return true;   // внутрибазовые
        return HasCrossDbIncoming(owner, collectionIds);
    }

    /// <summary>
    /// Консервативная проверка для операции, которая намеренно не сохраняет ни одного ребра.
    /// Обычный перенос коллекции/ветки теперь сохраняет граф и использует
    /// <see cref="ForCollectionsExternalCrossMove"/>; этот метод оставлен для совместимости.
    /// </summary>
    public static bool ForCrossMoveDroppingAllLinks(ConstructorRepository owner,
                                                    IReadOnlyCollection<int> collectionIds)
    {
        if (collectionIds.Count == 0) return false;
        if (owner.HasAnyLinksInCollections(collectionIds)) return true;
        return HasCrossDbIncoming(owner, collectionIds);
    }

    /// <summary>МЕЖБАЗОВЫЙ перенос ОДНОЙ коллекции: теряются только ВНЕШНИЕ входящие (само-связи внутри
    /// переписываются на новую копию). Межбазовые из других баз — всегда внешние.</summary>
    public static bool ForCollectionExternalCrossMove(ConstructorRepository owner, int collectionId) =>
        ForCollectionsExternalCrossMove(owner, [collectionId]);

    /// <summary>Перенос ветки сохраняет исходящие и внутренние связи. Обрываются только входящие
    /// из записей вне всего переносимого набора, включая ссылки из других открытых баз.</summary>
    public static bool ForCollectionsExternalCrossMove(ConstructorRepository owner,
                                                       IReadOnlyCollection<int> collectionIds)
    {
        if (collectionIds.Count == 0) return false;
        if (owner.HasExternalIncomingLinksToCollections(collectionIds)) return true;
        return HasCrossDbIncoming(owner, collectionIds);
    }

    // Есть ли межбазовое входящее ребро из какой-либо ОТКРЫТОЙ базы на записи данных коллекций.
    // Карта целей чужой базы в нашу мала (межбазовые связи разрежены) → пересечение дешево.
    private static bool HasCrossDbIncoming(ConstructorRepository owner, IReadOnlyCollection<int> collectionIds)
    {
        var ownerDbId = RelationDbResolver.DbIdFor(owner);
        if (ownerDbId <= 0) return false;   // без номера базы межбазовые не идентифицировать (коллизии id)
        foreach (var (_, repo) in RelationDbResolver.MountedReposExcept(ownerDbId))
        {
            var map = repo.IncomingLinkCountsByTarget(ownerDbId);
            if (map.Count > 0 && owner.RecordIdsInCollections(map.Keys.ToList(), collectionIds).Count > 0)
                return true;
        }
        return false;
    }

    // ── Кто ссылается (окно называет связи и учитывает запрет удаления) ───────────────────────

    /// <summary>Кто ссылается на удаляемые (<paramref name="move"/> = false) или переносимые записи:
    /// эта база и открытые чужие. Связь с запретом удаления блокирует действие
    /// (<see cref="RelationDeleteCheck.Blocked"/>), остальные называются в вопросе
    /// (<see cref="RelationDeleteCheck.Warning"/>). Считается в фоне на своих подключениях: окно не
    /// замирает и на больших базах.</summary>
    public static Task<RelationDeleteCheck> CheckRecordsAsync(ConstructorRepository owner, IReadOnlyCollection<int> recordIds,
        bool move = false) => CheckRecordsAsync([(owner, recordIds)], move);

    /// <summary>Несколько баз сразу (календарь и задачи собирают записи из всех баз). Источники,
    /// которые удаляются в той же операции, не считаются — и в своей базе, и в чужой.</summary>
    public static Task<RelationDeleteCheck> CheckRecordsAsync(
        IEnumerable<(ConstructorRepository Repo, IReadOnlyCollection<int> Ids)> groups, bool move = false)
    {
        var sets = groups.Where(g => g.Ids.Count > 0)
            .Select(g => (Base: Capture(g.Repo), Ids: (IReadOnlyCollection<int>)g.Ids.Distinct().ToList())).ToList();
        var mounted = Mounted();
        var total = sets.Sum(s => s.Ids.Count);
        return Task.Run(() =>
        {
            var items = new List<(string? Database, RelationReference Reference)>();
            foreach (var (owner, ids) in sets)
            {
                // Источники, удаляемые в той же операции в базе b, не считаются. Перенос копирует
                // записи с новыми номерами: ссылка между переносимыми обрывается так же, как ссылка
                // извне, поэтому при переносе считаются все.
                IReadOnlyCollection<int>? Together(BaseRef b) => move ? null
                    : sets.Where(s => s.Base.Path == b.Path).SelectMany(s => s.Ids).ToList();
                using (var repo = owner.Open())
                    items.AddRange(repo.Repo.IncomingReferences(ids, owner.Targets, Together(owner)).Select(r => ((string?)null, r)));
                if (owner.LinkId <= 0) continue;
                foreach (var other in mounted.Where(m => m.Path != owner.Path))
                {
                    using var repo = other.Open();
                    items.AddRange(repo.Repo.IncomingReferences(ids, [owner.LinkId], Together(other))
                        .Select(r => ((string?)other.Name, r)));
                }
            }
            return Check(items, move, total);
        });
    }

    /// <summary>То же для записей коллекций (удаление и перенос коллекции или ветки группы). Ссылки
    /// изнутри удаляемых или переносимых коллекций не считаются: при удалении они уходят вместе с
    /// ними, при переносе ветки переписываются на копию.</summary>
    public static Task<RelationDeleteCheck> CheckCollectionsAsync(
        IEnumerable<(ConstructorRepository Repo, IReadOnlyCollection<int> Collections)> groups, bool move = false)
    {
        var sets = groups.Where(g => g.Collections.Count > 0)
            .Select(g => (Base: Capture(g.Repo), Collections: (IReadOnlyCollection<int>)g.Collections.Distinct().ToList())).ToList();
        var mounted = Mounted();
        return Task.Run(() =>
        {
            var items = new List<(string? Database, RelationReference Reference)>();
            foreach (var (owner, collections) in sets)
            {
                IReadOnlyCollection<int> Together(BaseRef b) =>
                    sets.Where(s => s.Base.Path == b.Path).SelectMany(s => s.Collections).ToList();
                using (var repo = owner.Open())
                    items.AddRange(repo.Repo.IncomingReferencesToCollections(collections, Together(owner)).Select(r => ((string?)null, r)));
                if (owner.LinkId <= 0) continue;
                foreach (var other in mounted.Where(m => m.Path != owner.Path))
                {
                    using var source = other.Open();
                    // Карта межбазовых целей мала (такие связи разрежены): пересекаем с записями коллекций.
                    var map = source.Repo.IncomingLinkCountsByTarget(owner.LinkId);
                    if (map.Count == 0) continue;
                    List<int> targets;
                    using (var target = owner.Open()) targets = target.Repo.RecordIdsInCollections(map.Keys.ToList(), collections);
                    if (targets.Count > 0)
                        items.AddRange(source.Repo.IncomingReferences(targets, [owner.LinkId], excludedSourceCollections: Together(other))
                            .Select(r => ((string?)other.Name, r)));
                }
            }
            return Check(items, move, 0);
        });
    }

    private static RelationDeleteCheck Check(IReadOnlyList<(string? Database, RelationReference Reference)> items, bool move,
        int records)
    {
        if (items.Count == 0) return RelationDeleteCheck.None;
        var names = items.ToDictionary(i => i.Reference, i => i.Database, ReferenceEqualityComparer.Instance);
        string? Database(RelationReference r) => names.GetValueOrDefault(r);
        var restricted = items.Select(i => i.Reference).Where(r => r.Restrict).ToList();
        if (restricted.Count > 0)
            return new(ConstructorRepository.RestrictedMessage(restricted, move, records, Database), "", 0);
        return new(null, ConstructorRepository.DescribeReferenceList(items.Select(i => i.Reference), Database), items.Count);
    }

    // Снимок базы для фоновой проверки (берется на UI-потоке): общий контекст в фон не уходит.
    private sealed record BaseRef(int LinkId, string Path, PermissionService? Permissions, string? Name)
    {
        public int[] Targets => LinkId > 0 ? [0, LinkId] : [0];

        public OpenedBase Open()
        {
            var context = new AppDbContext(Path);
            return new(context, new ConstructorRepository(context) { Permissions = Permissions });
        }
    }

    private sealed record OpenedBase(AppDbContext Context, ConstructorRepository Repo) : IDisposable
    {
        public void Dispose() => Context.Dispose();
    }

    private static BaseRef Capture(ConstructorRepository repo)
    {
        var linkId = RelationDbResolver.DbIdFor(repo);
        return new(linkId, repo.DbPath, repo.Permissions, linkId > 0 ? RelationDbResolver.DbName(linkId) : null);
    }

    private static List<BaseRef> Mounted() => RelationDbResolver.MountedReposExcept(0)
        .Select(p => new BaseRef(p.DbId, p.Repo.DbPath, p.Repo.Permissions, RelationDbResolver.DbName(p.DbId))).ToList();
}

/// <summary>Итог проверки ссылок перед удалением или переносом. <see cref="Blocked"/> — текст отказа
/// (действие запрещено связью); <see cref="References"/> — перечень ссылок для вопроса («Счета ·
/// Клиент» (3 записи), …), пусто — ссылок нет; <see cref="Relations"/> — сколько связей в перечне.</summary>
public sealed record RelationDeleteCheck(string? Blocked, string References, int Relations)
{
    public static readonly RelationDeleteCheck None = new(null, "", 0);

    /// <summary>Дописка к вопросу: « На нее ссылается «Счета · Клиент» (3 записи) — эта связь будет
    /// удалена.» <paramref name="whom"/> — «нее», «них» или «записи»; при переносе связи
    /// обрываются.</summary>
    public string Warning(string whom, bool move = false) => References.Length == 0 ? "" :
        $" На {whom} {(Relations == 1 ? "ссылается" : "ссылаются")} {References} — " +
        (Relations == 1 ? move ? "эта связь оборвется." : "эта связь будет удалена."
                        : move ? "эти связи оборвутся." : "эти связи будут удалены.");
}
