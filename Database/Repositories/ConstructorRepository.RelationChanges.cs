using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;

namespace SmartBase.Database.Repositories;

/// <summary>Сохраненные данные. DbPath остается внутри процесса и не выводится пользователю.</summary>
public sealed record RelationDataChange(string DbPath, int DbId, int CollectionId,
    int? ChangedFieldId = null, int? RecordId = null);

public partial class ConstructorRepository
{
    public static event Action<RelationDataChange>? RelationDataChanged;

    private sealed record PendingRelationChange(RelationDataChange Change,
        Func<IReadOnlyList<Func<CancellationToken, IReadOnlyList<RelationAutoApplyResult>>>> Capture);
    private sealed class RelationChangesPending
    {
        internal readonly Dictionary<int, PendingRelationChange> Changes = new();
    }
    private static readonly ConditionalWeakTable<AppDbContext, RelationChangesPending> PendingRelationChanges = new();
    private static readonly ConditionalWeakTable<AppDbContext, ConstructorRepository> RelationChangeRepositories = new();

    /// <summary>После записи данных: UI сводит уведомления и считает в фоне, API без UI обновляет
    /// связи синхронно. Внутри внешней транзакции событие ждет ее успешного коммита.</summary>
    internal void NotifyRelationDataChanged(int collectionId, int? changedFieldId = null, int? recordId = null)
    {
        if (collectionId <= 0 || _savingAutoRelation || _refreshingAutoRelations) return;
        RelationChangeRepositories.GetValue(db, _ => this);
        try
        {
            var change = new RelationDataChange(DbPath, RelationDbResolver.DbIdFor(this), collectionId, changedFieldId, recordId);
            var pending = PendingRelationChanges.GetValue(db, _ => new());
            if (db.Database.CurrentTransaction is not null && pending.Changes.TryGetValue(collectionId, out var previous))
                change = change with
                {
                    ChangedFieldId = previous.Change.ChangedFieldId == changedFieldId ? changedFieldId : null,
                    RecordId = previous.Change.RecordId == recordId ? recordId : null,
                };
            var entry = new PendingRelationChange(change, () => CaptureRelationChangeWork(change));
            if (db.Database.CurrentTransaction is not null) pending.Changes[collectionId] = entry;
            else PublishRelationChange(entry);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Ручная правка уже сохранена. Сбой подбора не должен превращать ее в отказ записи.
            Serilog.Log.Warning(ex, "Не удалось подготовить обновление автоматических связей");
        }
    }

    private IReadOnlyList<Func<CancellationToken, IReadOnlyList<RelationAutoApplyResult>>> CaptureRelationChangeWork(RelationDataChange change)
    {
        var repositories = new List<ConstructorRepository> { this };

        // Без окна межбазовые тесты/API могут использовать scoped resolver. Не смешиваем
        // одинаковые реестровые id независимых рабочих пространств и временных тестовых баз.
        if (change.DbId <= 0 || RelationDbResolver.MountedRepoFor(change.DbId) is not { } changed
            || !string.Equals(changed.DbPath, change.DbPath, StringComparison.Ordinal))
            return [CaptureAutoRefreshGraph(repositories, [change], captureScopedTargets: true)];
        foreach (var pair in RelationChangeRepositories)
        {
            var repo = pair.Value;
            if (ReferenceEquals(repo, this) || repo.DbPath == DbPath) continue;
            var id = RelationDbResolver.DbIdFor(repo);
            if (id <= 0 || RelationDbResolver.MountedRepoFor(id)?.DbPath != repo.DbPath) continue;
            repositories.Add(repo);
        }
        return [CaptureAutoRefreshGraph(repositories, [change], captureScopedTargets: true)];
    }

    private static void PublishRelationChange(PendingRelationChange entry)
    {
        try
        {
            if (RelationDataChanged is { } handler) { handler(entry.Change); return; }
            foreach (var work in entry.Capture()) work(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Serilog.Log.Warning(ex, "Не удалось обновить автоматические связи; прежние цели сохранены");
        }
    }

    internal static void RelationTransactionStarted(AppDbContext context)
    {
        if (PendingRelationChanges.TryGetValue(context, out var pending)) pending.Changes.Clear();
    }

    internal static void RelationTransactionCommitted(AppDbContext context)
    {
        if (!PendingRelationChanges.TryGetValue(context, out var pending) || pending.Changes.Count == 0) return;
        var entries = pending.Changes.Values.ToArray();
        pending.Changes.Clear();
        foreach (var entry in entries) PublishRelationChange(entry);
    }

    internal static void RelationTransactionRolledBack(AppDbContext context) => RelationTransactionStarted(context);
}
