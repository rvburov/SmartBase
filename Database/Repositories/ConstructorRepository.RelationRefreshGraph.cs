using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    /// <summary>Владелец UI снимает только адреса и права. Граф зависимостей и данные читаются
    /// подготовленной работой на своих подключениях; порядок общий для всех открытых баз.</summary>
    public static Func<CancellationToken, IReadOnlyList<RelationAutoApplyResult>> CaptureAutoRefreshGraph(
        IReadOnlyList<ConstructorRepository> repositories, IReadOnlyList<RelationDataChange> changes,
        bool refreshAll = false, bool captureScopedTargets = false)
    {
        var descriptors = repositories.GroupBy(r => r.DbPath, StringComparer.Ordinal)
            .Select(g => g.First()).Select(r => new AutoConnection(RelationDbResolver.DbIdFor(r), r.DbPath, r.Permissions))
            .ToList();
        // API без окна может задавать только scoped resolver, без Workspace.Mounted. Этот
        // синхронный путь захватывает его адреса до замены resolver на рабочие подключения.
        if (captureScopedTargets)
            foreach (var repo in repositories)
                descriptors.AddRange(repo.CaptureAutoConnections(new DbField()).Targets.Values);
        var connections = descriptors.GroupBy(c => c.Path, StringComparer.Ordinal).Select(g => g.First()).ToArray();
        var savedChanges = changes.ToArray();
        return ct => RefreshAutoRelationGraph(connections, savedChanges, refreshAll, ct);
    }

    private sealed record AutoRefreshNode(ConstructorRepository Owner, DbField Field);

    private static IReadOnlyList<RelationAutoApplyResult> RefreshAutoRelationGraph(
        IReadOnlyList<AutoConnection> connections, IReadOnlyList<RelationDataChange> changes,
        bool refreshAll, CancellationToken ct)
    {
        var contexts = new List<AppDbContext>();
        var owners = new List<(AutoConnection Connection, ConstructorRepository Repo)>();
        try
        {
            foreach (var connection in connections)
            {
                ct.ThrowIfCancellationRequested();
                var context = new AppDbContext(connection.Path);
                contexts.Add(context);
                owners.Add((connection, new ConstructorRepository(context) { Permissions = connection.Permissions }));
            }
            var byId = owners.Where(o => o.Connection.DbId > 0).GroupBy(o => o.Connection.DbId)
                .ToDictionary(g => g.Key, g => g.First().Repo);
            using var resolver = RelationDbResolver.UseScopedResolver(id => byId.GetValueOrDefault(id),
                repo => owners.FirstOrDefault(o => o.Repo == repo || o.Connection.Path == repo.DbPath).Connection?.DbId ?? 0);
            var nodes = owners.SelectMany(o => o.Repo.AutoContext.DbFields.AsNoTracking()
                    .Where(f => f.FieldType == "relation").ToList().Select(ToDbField)
                    .Where(f => RelationConfig.FromConfig(f.ConfigJson)?.Auto is not null)
                    .Select(f => new AutoRefreshNode(o.Repo, f)))
                .OrderBy(n => n.Owner.DbPath, StringComparer.Ordinal).ThenBy(n => n.Field.Id).ToArray();
            if (nodes.Length == 0) return [];

            // Проверка всего графа происходит до первой записи. Включаем и собственное ребро:
            // lookup -> это же auto-поле также является циклом, хотя id полей различаются.
            var dependencies = new HashSet<int>[nodes.Length];
            var selected = new bool[nodes.Length];
            for (var i = 0; i < nodes.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var node = nodes[i];
                dependencies[i] = [];
                for (var other = 0; other < nodes.Length; other++)
                    if (node.Owner.AutoDependsOnChange(node.Field, nodes[other].Owner.DbPath,
                        nodes[other].Field.CollectionId, nodes[other].Field.Id)) dependencies[i].Add(other);
                selected[i] = refreshAll || changes.Any(c => node.Owner.AutoDependsOnChange(node.Field,
                    c.DbPath, c.CollectionId, c.ChangedFieldId));
            }
            var order = new List<int>(nodes.Length);
            var remaining = Enumerable.Range(0, nodes.Length).ToHashSet();
            while (remaining.Count > 0)
            {
                var ready = remaining.Where(i => !dependencies[i].Any(remaining.Contains)).Order().ToArray();
                if (ready.Length == 0)
                    return [new(RelationAutoStatus.InvalidRule, 0, "Автоматические связи образуют цикл между свойствами. Прежние связи сохранены.")];
                foreach (var index in ready) { order.Add(index); remaining.Remove(index); }
            }

            var results = new List<RelationAutoApplyResult>();
            var failed = new HashSet<int>();
            foreach (var owner in owners) owner.Repo._refreshingAutoRelations = true;
            foreach (var index in order)
            {
                ct.ThrowIfCancellationRequested();
                if (!selected[index]) continue;
                if (dependencies[index].Any(failed.Contains))
                {
                    failed.Add(index);
                    results.Add(new(RelationAutoStatus.Unavailable, 0, "Зависимое свойство не обновлено. Прежние связи сохранены."));
                    continue;
                }
                var node = nodes[index];
                var result = node.Owner.ApplyAutoRelation(node.Field.Id!.Value, ct: ct);
                results.Add(result);
                if (!result.Applied) failed.Add(index);
            }
            return results;
        }
        catch (OperationCanceledException)
        {
            return [new(RelationAutoStatus.Cancelled, 0, "Обновление связей отменено.")];
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or IOException or PermissionDeniedException)
        {
            // Недоступный каталог не позволяет доказать порядок зависимостей. Не запускаем
            // подбор по неполному графу и не считаем недоступные значения пустыми.
            return [new(RelationAutoStatus.Unavailable, 0, "Не удалось прочитать зависимости связей. Прежние цели сохранены.")];
        }
        finally
        {
            foreach (var owner in owners) owner.Repo._refreshingAutoRelations = false;
            foreach (var context in contexts) context.Dispose();
        }
    }
}
