using Avalonia.Threading;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Views;

public partial class MainWindow
{
    private readonly Dictionary<(string Path, int Collection), RelationDataChange> _relationChanges = new();
    private CancellationTokenSource _relationChangesCancellation = new();
    private HashSet<DatabaseManager> _relationMounted = [];
    private DispatcherTimer? _relationChangesTimer;
    private bool _relationChangesRunning;
    private bool _relationChangesClosed;
    private string? _relationFailureFingerprint;

    private void ConfigureRelationChanges()
    {
        ConstructorRepository.RelationDataChanged += OnRelationDataChanged;
        Closed += (_, _) =>
        {
            _relationChangesClosed = true;
            ConstructorRepository.RelationDataChanged -= OnRelationDataChanged;
            _relationChangesTimer?.Stop();
            _relationChangesCancellation.Cancel();
            _relationChanges.Clear();
        };
        QueueAllRelationRefreshes();
    }

    private void OnRelationDataChanged(RelationDataChange change) =>
        Dispatcher.UIThread.Post(() => QueueRelationChange(change));

    private void QueueRelationChange(RelationDataChange change)
    {
        if (_relationChangesClosed) return;
        var key = (change.DbPath, change.CollectionId);
        if (_relationChanges.TryGetValue(key, out var previous))
            change = change with
            {
                ChangedFieldId = previous.ChangedFieldId == change.ChangedFieldId ? change.ChangedFieldId : null,
                RecordId = previous.RecordId == change.RecordId ? change.RecordId : null,
            };
        _relationChanges[key] = change;
        _relationChangesTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _relationChangesTimer.Tick -= FlushRelationChanges;
        _relationChangesTimer.Tick += FlushRelationChanges;
        _relationChangesTimer.Stop();
        _relationChangesTimer.Start();
    }

    // У поллера нет надежных id измененных свойств; возвращение базы также могло пропустить
    // несколько правок. Проверяем правила всех открытых источников, сохраняя недоступные цели.
    private void QueueAllRelationRefreshes()
    {
        if (_relationChangesClosed) return;
        var mounted = _workspace.Mounted.Where(m => m.Permissions.AccessGranted).ToArray();
        if (!_relationMounted.SetEquals(mounted))
        {
            // Отключение/смена допуска отменяет работу, захватившую прежние подключения.
            _relationChangesCancellation.Cancel();
            _relationChangesCancellation = new();
            _relationMounted = mounted.ToHashSet();
        }
        // Нулевой id означает полную проверку правил и обновление вычислений/подписей.
        // Он нужен и тогда, когда auto-полей нет вообще: lookup и ручные связи тоже меняются.
        foreach (var manager in mounted) QueueRelationChange(new(manager.Path, manager.DbId, 0));
    }

    private async void FlushRelationChanges(object? sender, EventArgs args)
    {
        _relationChangesTimer?.Stop();
        if (_relationChangesClosed || _relationChangesRunning || _relationChanges.Count == 0) return;
        _relationChangesRunning = true;
        var changes = _relationChanges.Values.ToArray();
        _relationChanges.Clear();
        try
        {
            // Только адреса и права. Даже граф конфигов строится в фоне на своих подключениях.
            var mounted = _workspace.Mounted.Where(m => m.Permissions.AccessGranted).ToArray();
            var work = ConstructorRepository.CaptureAutoRefreshGraph(mounted.Select(m => m.Constructor).ToArray(),
                changes, refreshAll: changes.Any(c => c.CollectionId == 0));
            var token = _relationChangesCancellation.Token;
            var results = await Task.Run(() => work(token));
            if (!_relationChangesClosed && !token.IsCancellationRequested)
            {
                var failures = results.Where(r => !r.Applied && r.Status is not (RelationAutoStatus.Cancelled or RelationAutoStatus.NoRule or RelationAutoStatus.Ready))
                    .Select(r => (r.Status, r.Message)).Distinct().OrderBy(r => r.Status).ThenBy(r => r.Message, StringComparer.Ordinal).ToArray();
                var fingerprint = string.Join("\n", failures.Select(f => $"{f.Status}:{f.Message}"));
                if (failures.Length > 0 && fingerprint != _relationFailureFingerprint)
                {
                    _relationFailureFingerprint = fingerprint;
                    var reason = string.Join("\n", failures.Select(f => f.Message).Distinct(StringComparer.Ordinal));
                    Serilog.Log.Warning("Автоматические связи не обновлены: {Reason}", reason);
                    Notify(reason);
                }
                else if (failures.Length == 0 && results.Any(r => r.Applied)) _relationFailureFingerprint = null;
            }
            if (!_relationChangesClosed)
                foreach (var dbId in mounted.Select(m => m.DbId))
                    ApplyLocalChanges(dbId, structural: false, ChangeBus.Now());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Serilog.Log.Warning(ex, "Не удалось запланировать фоновый подбор связей");
        }
        finally
        {
            _relationChangesRunning = false;
            if (!_relationChangesClosed && _relationChanges.Count > 0) _relationChangesTimer?.Start();
        }
    }
}
