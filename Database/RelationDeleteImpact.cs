using SmartBase.Database.Repositories;

namespace SmartBase.Database;

/// <summary>
/// Есть ли ВХОДЯЩИЕ связи (тип «relation»), которые оборвет удаление / перенос записей / коллекции /
/// группы — для окна подтверждения «связи будут удалены» (без числа: нужен лишь факт). Проверяет
/// внутрибазовые (TargetDbId=0, чистятся сразу) и межбазовые из ДРУГИХ ОТКРЫТЫХ баз (обрываются →
/// «связи нет», лениво). Закрытые базы не проверяем (их ребра вычистятся при открытии) — best-effort.
/// Any/EXISTS обрывается на первом ребре, поэтому дешево даже на больших базах. Общие репозитории →
/// звать только с UI-потока.
/// </summary>
public static class RelationDeleteImpact
{
    /// <summary>Есть ли входящие связи на записи <paramref name="recordIds"/> (удаление/перенос записи).</summary>
    public static bool ForRecords(ConstructorRepository owner, int ownerDbId, IReadOnlyCollection<int> recordIds)
    {
        if (recordIds.Count == 0) return false;
        if (owner.HasIncomingLinks(recordIds, 0)) return true;                 // внутрибазовые
        // Межбазовые ищем ТОЛЬКО при валидном DbId нашей базы: чужая база различает ребра «в нашу базу»
        // по TargetDbId == ownerDbId. Если ownerDbId неизвестен (≤0), пропускаем — иначе с targetDbId=0
        // совпали бы ВНУТРЕННИЕ ребра чужой базы по коллизии id (у баз id могут пересекаться).
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

    /// <summary>
    /// Перенос, который ребра НЕ копирует ВООБЩЕ — ни входящие, ни исходящие, ни само-связи внутри
    /// переносимого. Таких путей два:
    /// <list type="bullet">
    /// <item>любой перенос с участием PostgreSQL (generic-путь: ATTACH есть только у SQLite);</item>
    /// <item>перенос ВЕТКИ ГРУППЫ между базами даже на SQLite — <c>CopyGroupFromAttached</c> зовет
    /// копирование коллекций с <c>sourceDbId/targetDbId = 0,0</c> (v1: межколлекционный ремап
    /// <c>ToRecordId</c> внутри ветки не сделан).</item>
    /// </list>
    /// Поэтому предупреждаем при наличии ЛЮБЫХ связей, а не только внешних входящих: коллекция с
    /// одними само-связями и ветка, ссылающаяся ТОЛЬКО наружу, прошли бы обычную проверку
    /// («внешних входящих нет») и потеряли бы связи молча.
    /// </summary>
    public static bool ForCrossMoveDroppingAllLinks(ConstructorRepository owner, int ownerDbId,
                                                    IReadOnlyCollection<int> collectionIds)
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
    // Карта целей чужой базы в нашу мала (межбазовые связи разрежены) → пересечение дешево.
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
