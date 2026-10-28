using Avalonia.Controls;
using Avalonia.Threading;
using SmartBase.Controls.Dialogs;
using SmartBase.Core.Models;
using SmartBase.Utils;
using SmartBase.ViewModels.ConstructorDb;

namespace SmartBase.Views.ConstructorDb;

public partial class TableView
{
    private readonly DispatcherTimer _datePeriodTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private CancellationTokenSource? _datePeriodRefreshCancellation;
    private bool _datePeriodRecalculating;

    private void InitializeDatePeriodRuntime()
    {
        _datePeriodTimer.Tick += async (_, _) => await RefreshDatePeriodsForTimeAsync();
        RecordsGrid.RecordEdited += (_, _) => _datePeriodRefreshCancellation?.Cancel();
        RecordsGrid.RowsReplacing += (_, _) => _datePeriodRefreshCancellation?.Cancel();
    }

    // Один таймер на таблицу. Читаем только вычисляемые значения видимых записей:
    // не пересортировываем коллекцию, не закрываем редактор и не пишем значения в БД.
    private async Task RefreshDatePeriodsForTimeAsync()
    {
        if (_repository is null || !IsEffectivelyVisible || VisualRoot is null
            || _datePeriodRefreshCancellation is not null || _datePeriodRecalculating) return;
        var fields = _fields.Where(field => field.FieldType == "date_period"
            && DatePeriodConfig.FromConfig(field.ConfigJson) is { Automatic: true, EndFieldId: null })
            .Select(field => field.Id!.Value).ToArray();
        if (fields.Length == 0 || RecordsGrid.Rows is not { } rows) return;
        var (first, last) = RecordsGrid.Surface.VisibleRows();
        var ids = Enumerable.Range(Math.Max(0, first), Math.Max(0, last - Math.Max(0, first) + 1))
            .Select(index => rows.TryGetCached(index)?.RecordId ?? 0).Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return;
        var repository = _repository;
        var collection = _collectionId;
        using var cancellation = new CancellationTokenSource();
        _datePeriodRefreshCancellation = cancellation;
        try
        {
            var values = await repository.ReadDatePeriodsAsync(ids, fields, cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_repository, repository)
                || _collectionId != collection || !ReferenceEquals(RecordsGrid.Rows, rows)
                || !IsEffectivelyVisible || VisualRoot is null || _datePeriodRecalculating) return;
            foreach (var (recordId, byField) in values) rows.FindCachedByRecordId(recordId)?.MergeValues(byField);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            DbErrors.HandleRead(ex, repository.DbPath);
            if (ex is not PermissionDeniedException)
                Serilog.Log.Warning(ex, "Не удалось обновить периоды времени в видимых строках");
        }
        finally
        {
            if (ReferenceEquals(_datePeriodRefreshCancellation, cancellation)) _datePeriodRefreshCancellation = null;
        }
    }

    private void AddDatePeriodRecalculation(ContextMenu menu, Func<TableRowVm?> row)
    {
        if (!_fields.Any(field => field.FieldType == "date_period"
            && DatePeriodConfig.FromConfig(field.ConfigJson) is { Automatic: false })) return;
        var item = new MenuItem { Header = "Пересчитать" };
        Guarded(item, PermAction.EditRecord, () => _ = RecalculateDatePeriodsAsync(TargetRecordIds(row)));
        menu.Items.Add(item);
    }

    public async Task RecalculateDatePeriodsAsync(IReadOnlyList<int> recordIds, IReadOnlyList<int>? fieldIds = null)
    {
        if (_repository is null || recordIds.Count == 0 || _datePeriodRecalculating) return;
        _datePeriodRefreshCancellation?.Cancel();
        CommitCellEdit();
        _datePeriodRecalculating = true;
        var repository = _repository;
        var collection = _collectionId;
        var window = VisualRoot as Window;
        using var cancellation = new CancellationTokenSource();
        ProgressDialog? progress = null;
        try
        {
            var work = repository.RecalculateDatePeriodsAsync(recordIds, fieldIds, cancellation.Token);
            Task? closed = null;
            if (window is not null && await Task.WhenAny(work, Task.Delay(400)) != work)
            {
                progress = new ProgressDialog { Title = "Дата период времени" };
                progress.SetSaving("Пересчитываю выбранные записи…");
                progress.EnableCancel(cancellation.Cancel);
                closed = progress.ShowDialog(window);
            }
            try { await work; }
            finally { progress?.Done(); }
            if (closed is not null) await closed;
            if (!ReferenceEquals(_repository, repository) || _collectionId != collection) return;
            ReloadRows();
            foreach (var id in recordIds) RecordValueEdited?.Invoke(this, id);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (PermissionDeniedException)
        {
            PermissionNotice.Run(this, false, PermAction.EditRecord, () => { });
        }
        catch (Exception ex)
        {
            if (!DbErrors.Handle(ex, repository.DbPath) && window is not null)
                await ConfirmDialog.Info(ex.Message).ShowDialog(window);
        }
        finally { progress?.Done(); _datePeriodRecalculating = false; }
    }
}
