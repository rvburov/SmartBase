using Avalonia.Threading;
using SmartBase.Core.Models;
using SmartBase.Database;

namespace SmartBase.Views;

public partial class MainWindow
{
    private HashSet<DatabaseManager> _integrityMounted = [];
    private readonly Dictionary<int, string> _integrityNotices = [];
    private CancellationTokenSource _integrityCancellation = new();
    private DispatcherTimer? _integrityTimer;
    private bool _integrityRunning;
    private bool _integrityClosed;
    private bool _integrityPending;

    private void ConfigureRelationIntegrity()
    {
        Closed += (_, _) =>
        {
            _integrityClosed = true;
            _integrityTimer?.Stop();
            _integrityCancellation.Cancel();
        };
        QueueRelationIntegrityChecks();
    }

    // Старт и возвращение базы требуют нового снимка. Закрытые базы не открываем:
    // отсутствие подключения не доказывает потерю цели. Стартовая проверка ничего не исправляет.
    private void QueueRelationIntegrityChecks(bool force = false)
    {
        if (_integrityClosed) return;
        var mounted = _workspace.Mounted.ToHashSet();
        if (!force && _integrityMounted.SetEquals(mounted)) return;
        _integrityMounted = mounted;
        foreach (var dbId in _integrityNotices.Keys.Where(id => mounted.All(db => db.DbId != id)).ToArray())
            _integrityNotices.Remove(dbId);
        _integrityCancellation.Cancel();
        _integrityCancellation = new();
        _integrityPending = true;
        _integrityTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _integrityTimer.Tick -= RunRelationIntegrityChecks;
        _integrityTimer.Tick += RunRelationIntegrityChecks;
        _integrityTimer.Stop();
        _integrityTimer.Start();
    }

    private async void RunRelationIntegrityChecks(object? sender, EventArgs args)
    {
        _integrityTimer?.Stop();
        if (_integrityClosed || _integrityRunning || !_integrityPending) return;
        _integrityPending = false;
        _integrityRunning = true;
        var token = _integrityCancellation.Token;
        // Захватываем список подключенных баз. Чтения таблиц делает Scan на своем фоне.
        var mounted = _integrityMounted.ToArray();
        try
        {
            foreach (var manager in mounted)
            {
                token.ThrowIfCancellationRequested();
                var report = await manager.Constructor.ScanRelationIntegrityAsync(ct: token);
                if (_integrityClosed || token.IsCancellationRequested) return;
                if (report.Status != RelationIntegrityStatus.Completed) continue;
                if (report.ConfirmedIssues == 0 && report.UnverifiedIssues == 0)
                {
                    _integrityNotices.Remove(manager.DbId);
                    continue;
                }
                // Переименование другого пользователя обновляет справочник. Сами замечания
                // от этого не меняются: повторяем карточку только при смене их состава.
                var stamp = report.SourceDatabaseIdentity + ":" +
                    string.Join(";", report.Counts.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}")) + ":" +
                    string.Join(";", report.Issues.Where(i => !i.IsInformational).Select(i => i.Key));
                if (_integrityNotices.GetValueOrDefault(manager.DbId) == stamp) continue;
                _integrityNotices[manager.DbId] = stamp;
                Serilog.Log.Information("Проверка связей базы {DbId}: подтверждено {Confirmed}, не проверено {Unverified}",
                    manager.DbId, report.ConfirmedIssues, report.UnverifiedIssues);
                Notify($"Связи базы «{manager.Name}»: проблем — {report.ConfirmedIssues}, " +
                       $"не проверено — {report.UnverifiedIssues}. Откройте «Настройки → Управление базой данных → Проверить связи».");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Адрес подключения и текст исключения могут содержать параметры доступа.
            Serilog.Log.Warning("Фоновая проверка связей не завершена: {ErrorType}", ex.GetType().Name);
        }
        finally
        {
            _integrityRunning = false;
            if (!_integrityClosed && _integrityPending) _integrityTimer?.Start();
        }
    }
}
