using System.Security.Cryptography;
using System.Text;
using SmartBase.Core.Models;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    // Непрозрачная идентичность: строки подключения могут содержать пароль и не экспортируются.
    public string RelationDatabaseIdentity => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DbPath)));

    public Dictionary<int, string> GetRelationDatabaseIdentities(IEnumerable<RelationTarget> targets)
    {
        var result = new Dictionary<int, string>();
        var ownDbId = RelationDbResolver.DbIdFor(this);
        foreach (var id in targets.Select(t => t.DbId).Where(id => id > 0).Distinct())
            if (id == ownDbId) result[id] = RelationDatabaseIdentity;
            else if (RelationDbResolver.RepoFor(id) is { } repo) result[id] = repo.RelationDatabaseIdentity;
        return result;
    }

    public IReadOnlyList<RelationTarget> RebaseRelationClipboardTargets(RelationClipboard payload,
        IReadOnlyList<RelationTarget> targets)
    {
        var sameSource = payload.SourceIdentity == RelationDatabaseIdentity;
        if (!sameSource && (payload.SourceDbId <= 0 || RelationDbResolver.RepoFor(payload.SourceDbId)?.RelationDatabaseIdentity != payload.SourceIdentity))
            throw new InvalidOperationException("Исходная база связей не найдена. Вставка по идентификаторам отменена.");
        var ownDbId = RelationDbResolver.DbIdFor(this);
        var result = new List<RelationTarget>(targets.Count);
        foreach (var target in targets)
        {
            if (target.DbId == 0 || payload.SourceDbId > 0 && target.DbId == payload.SourceDbId)
                result.Add(new(sameSource ? 0 : payload.SourceDbId, target.RecordId));
            else
            {
                if (!payload.DatabaseIdentities.TryGetValue(target.DbId, out var identity)
                    || RelationDbResolver.RepoFor(target.DbId)?.RelationDatabaseIdentity != identity)
                    throw new InvalidOperationException("Не удалось проверить целевую базу связи. Вставка отменена.");
                result.Add(new(ownDbId > 0 && target.DbId == ownDbId ? 0 : target.DbId, target.RecordId));
            }
        }
        return result;
    }
}
