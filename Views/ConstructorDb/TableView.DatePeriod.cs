using Avalonia.Controls;
using SmartBase.Controls.Dialogs;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Utils;
using SmartBase.ViewModels.ConstructorDb;

namespace SmartBase.Views.ConstructorDb;

public partial class TableView
{
    private bool _datePeriodRecalculating;
    private Task? _datePeriodQueryRefresh;
    private CancellationTokenSource? _datePeriodQueryCancellation;
    private int _datePeriodQueryRevision;

    private void InitializeDatePeriodRuntime()
    {
        // Значения диапазона приходят с ленивой страницей и побочными изменениями
        // записи. Повторять запрос нужно только при реальной правке, если период
        // влияет на состав или порядок строк, в том числе у скрытой колонки.
        RecordsGrid.RecordEdited += (_, recordId) =>
        {
            if (RecordsGrid.Rows?.FindCachedByRecordId(recordId)?.LastEditedFieldId is int fieldId
                && HasAutomaticDatePeriodQuery(fieldId)) QueueDatePeriodQueryRefresh();
        };
        DetachedFromVisualTree += (_, _) => CancelDatePeriodQueryRefresh();
    }

    private bool HasAutomaticDatePeriodQuery(int? sourceFieldId = null)
    {
        var keys = _filters.Keys.Append(_sortKey ?? "").ToHashSet();
        return _repository is not null && _fields.Any(field => field.FieldType == "date_period"
            && field.Id is int id && keys.Contains(id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            && DatePeriodConfig.FromConfig(field.ConfigJson) is { Automatic: true } config
            && (sourceFieldId is null || config.DateFieldId == sourceFieldId));
    }

    private bool CanRefreshDatePeriodQuery() => !IsCellEditing && !_dragging && !_dragSelecting
        && !RecordsGrid.IsLayoutInteractionActive && !_datePeriodRecalculating;

    private void CancelDatePeriodQueryRefresh()
    {
        _datePeriodQueryRevision++;
        _datePeriodQueryCancellation?.Cancel();
        _datePeriodQueryCancellation = null;
        _datePeriodQueryRefresh = null;
    }

    private void QueueDatePeriodQueryRefresh()
    {
        if (!HasAutomaticDatePeriodQuery()) return;
        _datePeriodQueryRevision++;
        if (_datePeriodQueryRefresh is not null) return;
        var cancellation = new CancellationTokenSource();
        _datePeriodQueryCancellation = cancellation;
        var work = RefreshDatePeriodQueryAsync(_repository!, _collectionId, cancellation);
        // Простая выборка может завершиться синхронно — завершенный task не должен
        // перекрыть очистку состояния в finally и заблокировать следующую правку.
        _datePeriodQueryRefresh = work.IsCompleted ? null : work;
    }

    private async Task RefreshDatePeriodQueryAsync(ConstructorRepository repository, int collection,
                                                   CancellationTokenSource cancellation)
    {
        bool Current() => !cancellation.IsCancellationRequested
            && ReferenceEquals(_repository, repository) && _collectionId == collection;
        try
        {
            while (Current() && HasAutomaticDatePeriodQuery())
            {
                var revision = _datePeriodQueryRevision;
                // Собираем правки одной операции в один запрос; ввод или жест не
                // прерываем подменой строк. Здесь нет таймера расчета значений.
                await RecordsGrid.QueryTask.WaitAsync(cancellation.Token);
                while (Current() && !CanRefreshDatePeriodQuery())
                    await Task.Delay(100, cancellation.Token);
                if (!Current() || !HasAutomaticDatePeriodQuery()) return;
                await RecordsGrid.RefreshQuery(() => Current() && CanRefreshDatePeriodQuery(), cancellation.Token);
                if (revision == _datePeriodQueryRevision) return;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            DbErrors.HandleRead(ex, repository.DbPath);
            Serilog.Log.Warning(ex, "Не удалось обновить отбор по периоду времени после изменения записи");
        }
        finally
        {
            if (ReferenceEquals(_datePeriodQueryCancellation, cancellation))
            {
                _datePeriodQueryCancellation = null;
                _datePeriodQueryRefresh = null;
            }
            cancellation.Dispose();
        }
    }

    private void AddDatePeriodRecalculation(ContextMenu menu, Func<TableRowVm?> row)
    {
        if (!_fields.Any(field => field.FieldType == "date_period"
            && DatePeriodConfig.FromConfig(field.ConfigJson) is { Automatic: false })) return;
        var item = new MenuItem { Header = "Пересчитать" };
        item.Click += (_, _) => _ = RecalculateDatePeriodsAsync(TargetRecordIds(row));
        menu.Items.Add(item);
    }

    public async Task RecalculateDatePeriodsAsync(IReadOnlyList<int> recordIds, IReadOnlyList<int>? fieldIds = null)
    {
        if (_repository is null || recordIds.Count == 0 || _datePeriodRecalculating) return;
        CancelDatePeriodQueryRefresh();
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
        catch (Exception ex)
        {
            if (!DbErrors.Handle(ex, repository.DbPath) && window is not null)
                await ConfirmDialog.Info(ex.Message).ShowDialog(window);
        }
        finally
        {
            progress?.Done();
            _datePeriodRecalculating = false;
        }
    }
}
