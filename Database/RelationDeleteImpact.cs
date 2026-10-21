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
}
