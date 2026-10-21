using Avalonia.Threading;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Views;

public partial class MainWindow
{
    private CancellationTokenSource _relationBaseCancellation = new();
    private bool _relationBaseRunning;
    private bool _relationBasePending;
    private bool _relationBaseClosed;

    private void ConfigureRelationBases()
    {
        Closed += (_, _) =>
        {
            _relationBaseClosed = true;
            _relationBaseCancellation.Cancel();
        };
        QueueRelationBaseMigration();
    }

    // Набор подключенных баз сменился: запомнить номера баз для связей (по ним база находится,
    // даже пока она еще не подключена) и перевести старые межбазовые связи на эти номера. Перевод
    // ждет, пока очередь подключения опустеет: решение «эта база — единственная подходящая»
    // принимается по всем базам, которые этот компьютер вообще может открыть.
    private void QueueRelationBaseMigration()
    {
        if (_relationBaseClosed) return;
        try { RelationDbResolver.SaveKnownBases(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Serilog.Log.Warning("Номера баз для связей не сохранены: {ErrorType}", ex.GetType().Name);
        }
        if (_mountQueue.Pending().Any(s => s.State != MountQueue.ItemState.Failed)) return;
        _relationBasePending = true;
        if (!_relationBaseRunning) Dispatcher.UIThread.Post(RunRelationBaseMigration, DispatcherPriority.Background);
    }

    private async void RunRelationBaseMigration()
    {
        if (_relationBaseClosed || _relationBaseRunning || !_relationBasePending) return;
        _relationBasePending = false;
        _relationBaseRunning = true;
        _relationBaseCancellation.Cancel();
        _relationBaseCancellation = new();
        var token = _relationBaseCancellation.Token;
        // Только адреса и номера; чтение и запись — на своих подключениях в фоне.
        var mounted = _workspace.Mounted
            .Select(m => new RelationBaseCandidate(m.DbId, m.Constructor.RelationBaseId, m.Context.DbPath)).ToArray();
        var migrated = 0;
        try
        {
            migrated = await Task.Run(() =>
            {
                var total = 0;
                foreach (var source in mounted)
                {
                    token.ThrowIfCancellationRequested();
                    try { total += ConstructorRepository.MigrateLegacyRelationTargets(source.Path, mounted, token).Migrated; }
                    catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
                    {
                        // Занятая или недоступная база переведется при следующем подключении.
                        Serilog.Log.Warning("Старые межбазовые связи базы {DbId} не переведены: {ErrorType}",
                            source.RegistryId, ex.GetType().Name);
                    }
                }
                return total;
            }, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            _relationBaseRunning = false;
        }
        if (_relationBaseClosed) return;
        if (migrated > 0)
        {
            foreach (var dbId in mounted.Select(m => m.RegistryId))
                ApplyLocalChanges(dbId, structural: true, ChangeBus.Now());
        }
        if (_relationBasePending) Dispatcher.UIThread.Post(RunRelationBaseMigration, DispatcherPriority.Background);
    }
}
