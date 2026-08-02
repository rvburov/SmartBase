using SmartBase.Database.Repositories;

namespace SmartBase.Database;

/// <summary>
/// Есть ли ВХОДЯЩИЕ связи (тип «relation»), которые оборвёт удаление / перенос записей / коллекции /
/// группы — для окна подтверждения «связи будут удалены» (без числа: нужен лишь факт). Проверяет
/// внутрибазовые (TargetDbId=0, чистятся сразу) и межбазовые из ДРУГИХ ОТКРЫТЫХ баз (обрываются →
/// «связи нет», лениво). Закрытые базы не проверяем (их рёбра вычистятся при открытии) — best-effort.
/// Any/EXISTS обрывается на первом ребре, поэтому дёшево даже на больших базах. Общие репозитории →
/// звать только с UI-потока.
/// </summary>
public static class RelationDeleteImpact
{
    /// <summary>Есть ли входящие связи на записи <paramref name="recordIds"/> (удаление/перенос записи).</summary>
    public static bool ForRecords(ConstructorRepository owner, int ownerDbId, IReadOnlyCollection<int> recordIds)
    {
        if (recordIds.Count == 0) return false;
        if (owner.HasIncomingLinks(recordIds, 0)) return true;                 // внутрибазовые
        // Межбазовые ищем ТОЛЬКО при валидном DbId нашей базы: чужая база различает рёбра «в нашу базу»
        // по TargetDbId == ownerDbId. Если ownerDbId неизвестен (≤0), пропускаем — иначе с targetDbId=0
        // совпали бы ВНУТРЕННИЕ рёбра чужой базы по коллизии id (у баз id могут пересекаться).
        if (ownerDbId <= 0) return false;
        foreach (var (_, repo) in RelationDbResolver.MountedReposExcept(ownerDbId))
            if (repo.HasIncomingLinks(recordIds, ownerDbId)) return true;      // межбазовые из открытых баз
        return false;
    }

    /// <summary>Есть ли входящие связи на записи коллекций <paramref name="collectionIds"/>
    /// (удаление коллекции/группы; в т.ч. ветка группы).</summary>
    public static bool ForCollections(ConstructorRepository owner, int ownerDbId, IReadOnlyCollection<int> collectionIds)
    {
        if (collectionIds.Count == 0) return false;
        if (owner.HasIncomingLinksToCollections(collectionIds)) return true;   // внутрибазовые
        return HasCrossDbIncoming(owner, ownerDbId, collectionIds);
    }

    /// <summary>Удаляется/переносится (группа отбрасывает ВСЕ связи ветки) группа <paramref name="groupId"/>.</summary>
    public static bool ForGroup(ConstructorRepository owner, int ownerDbId, int groupId) =>
        ForCollections(owner, ownerDbId, owner.CollectionIdsInGroup(groupId));

    /// <summary>Перенос с участием PostgreSQL (generic-путь вместо ATTACH): рёбра НЕ копируются вовсе —
    /// ни входящие, ни исходящие, ни само-связи внутри переносимого. Поэтому предупреждаем при наличии
    /// ЛЮБЫХ связей, а не только внешних входящих: коллекция с одними само-связями прошла бы обычную
    /// проверку («внешних нет») и потеряла бы связи молча.</summary>
    public static bool ForPgCrossMove(ConstructorRepository owner, int ownerDbId, IReadOnlyCollection<int> collectionIds)
    {
        if (collectionIds.Count == 0) return false;
        if (owner.HasAnyLinksInCollections(collectionIds)) return true;
        return HasCrossDbIncoming(owner, ownerDbId, collectionIds);
    }

    /// <summary>МЕЖБАЗОВЫЙ перенос ОДНОЙ коллекции: теряются только ВНЕШНИЕ входящие (само-связи внутри
    /// переписываются на новую копию). Межбазовые из других баз — всегда внешние.</summary>
    public static bool ForCollectionExternalCrossMove(ConstructorRepository owner, int ownerDbId, int collectionId)
    {
        if (owner.HasExternalIncomingLinksToCollection(collectionId)) return true;
        return HasCrossDbIncoming(owner, ownerDbId, [collectionId]);
    }

    // Есть ли межбазовое входящее ребро из какой-либо ОТКРЫТОЙ базы на записи данных коллекций.
    // Карта целей чужой базы в нашу мала (межбазовые связи разрежены) → пересечение дёшево.
    private static bool HasCrossDbIncoming(ConstructorRepository owner, int ownerDbId, IReadOnlyCollection<int> collectionIds)
    {
        if (ownerDbId <= 0) return false;   // без валидного DbId межбазовые не идентифицировать (коллизии id)
        foreach (var (_, repo) in RelationDbResolver.MountedReposExcept(ownerDbId))
        {
            var map = repo.IncomingLinkCountsByTarget(ownerDbId);
            if (map.Count > 0 && owner.RecordIdsInCollections(map.Keys.ToList(), collectionIds).Count > 0)
                return true;
        }
        return false;
    }
}
