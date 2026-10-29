using Avalonia.Controls;
using Avalonia.Threading;
using SmartBase.Controls.Dialogs;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Utils;
using SmartBase.ViewModels.ConstructorDb;

namespace SmartBase.Views.ConstructorDb;

public partial class TableView
{
    private readonly DispatcherTimer _datePeriodTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly DispatcherTimer _datePeriodViewportTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private CancellationTokenSource? _datePeriodRefreshCancellation;
    private bool _datePeriodRecalculating;
    private bool _datePeriodViewportAttached, _datePeriodViewportDirty;
    private int _datePeriodViewportVersion;
    private DatePeriodViewportState? _datePeriodLastViewport;
    private Task? _datePeriodQueryRefresh;
    private CancellationTokenSource? _datePeriodQueryCancellation;
    private DatePeriodQueryState? _datePeriodLastQuery;

    private sealed record DatePeriodQueryState(ConstructorRepository Repository, int Collection, long Minute, string Rule);

    private sealed record DatePeriodViewportState(ConstructorRepository Repository, VirtualRowList Rows,
        int Collection, int Version, long Minute, int[] Records, int[] Fields)
    {
        internal bool Matches(DatePeriodViewportState other) => ReferenceEquals(Repository, other.Repository)
            && ReferenceEquals(Rows, other.Rows) && Collection == other.Collection && Version == other.Version
            && Minute == other.Minute && Records.AsSpan().SequenceEqual(other.Records)
            && Fields.AsSpan().SequenceEqual(other.Fields);
    }

    private void InitializeDatePeriodRuntime()
    {
        _datePeriodTimer.Tick += async (_, _) => await RefreshDatePeriodsForTimeAsync();
        _datePeriodViewportTimer.Tick += async (_, _) =>
        {
            _datePeriodViewportTimer.Stop();
            if (!_datePeriodViewportDirty) return;
            _datePeriodViewportDirty = false;
            await RefreshDatePeriodsAsync(force: false);
        };
        RecordsGrid.Surface.OffsetChanged += (_, _) => QueueDatePeriodViewportRefresh();
        RecordsGrid.Surface.ViewChanged += (_, _) => QueueDatePeriodViewportRefresh();
        // После загрузки страницы среди прежних видимых индексов появились настоящие id.
        RecordsGrid.Surface.ExtentChanged += (_, _) => QueueDatePeriodViewportRefresh();
        RecordsGrid.RecordEdited += (_, _) => InvalidateDatePeriodViewport();
        RecordsGrid.RowsReplacing += (_, _) => InvalidateDatePeriodViewport();
        AttachedToVisualTree += (_, _) =>
        {
            _datePeriodViewportAttached = true;
            QueueDatePeriodViewportRefresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _datePeriodViewportAttached = _datePeriodViewportDirty = false;
            _datePeriodViewportTimer.Stop();
            _datePeriodLastViewport = null;
            _datePeriodViewportVersion++;
            _datePeriodRefreshCancellation?.Cancel();
            _datePeriodQueryCancellation?.Cancel();
            _datePeriodLastQuery = null;
        };
    }

    private void InvalidateDatePeriodViewport()
    {
        _datePeriodLastViewport = null;
        _datePeriodViewportVersion++;
        _datePeriodRefreshCancellation?.Cancel();
        QueueDatePeriodViewportRefresh();
    }

    private void QueueDatePeriodViewportRefresh()
    {
        if (!_datePeriodViewportAttached || !IsEffectivelyVisible || VisualRoot is null) return;
        _datePeriodViewportDirty = true;
        // События разметки могут идти непрерывно. Объединяем их, не отодвигая
        // запуск навсегда; совпавшая сигнатура ниже не выполнит повторный SQL.
        if (!_datePeriodViewportTimer.IsEnabled) _datePeriodViewportTimer.Start();
    }

    // Читаем только вычисляемые значения видимых записей и колонок:
    // не закрываем редактор и не пишем значения в БД. Активный отбор по текущему
    // времени обновляется отдельно: ему нужны и ранее не попавшие в окно записи.
    private async Task RefreshDatePeriodsForTimeAsync()
    {
        await RefreshDatePeriodQueryForTimeAsync();
        await RefreshDatePeriodsAsync(force: true);
    }

    private bool CanRefreshDatePeriodQuery() => !IsCellEditing && !_dragging && !_dragSelecting
        && !RecordsGrid.IsLayoutInteractionActive && !_datePeriodRecalculating;

    private DatePeriodQueryState? CurrentDatePeriodQuery()
    {
        if (_repository is null || !_datePeriodViewportAttached || !IsEffectivelyVisible || VisualRoot is null) return null;
        var keys = _filters.Keys.Append(_sortKey ?? "").ToHashSet();
        var fields = _fields.Where(field => keys.Contains(field.Id!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
            && field.FieldType == "date_period"
            && DatePeriodConfig.FromConfig(field.ConfigJson) is { Automatic: true, EndFieldId: null }).ToArray();
        if (fields.Length == 0) return null;
        var rule = System.Text.Json.JsonSerializer.Serialize(new
        {
            Sort = _sortKey, Asc = _sortAsc,
            Filters = _filters.OrderBy(pair => pair.Key).Select(pair => new
                { pair.Key, Values = pair.Value.OrderBy(value => value).ToArray() }).ToArray(),
            Fields = fields.Select(field => new { field.Id, field.ConfigJson }).ToArray(),
        });
        return new(_repository, _collectionId, _repository.DatePeriodClock().Ticks / TimeSpan.TicksPerMinute, rule);
    }

    private async Task RefreshDatePeriodQueryForTimeAsync()
    {
        if (_datePeriodQueryRefresh is { } pending)
        {
            // Владелец общего запроса ниже обрабатывает и сообщает его ошибку.
            try { await pending; }
            catch (Exception) { }
            return;
        }
        var requested = CurrentDatePeriodQuery();
        if (requested is null || requested == _datePeriodLastQuery) return;
        using var cancellation = new CancellationTokenSource();
        _datePeriodQueryCancellation = cancellation;
        var work = Refresh();
        _datePeriodQueryRefresh = work;
        try { await work; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            DbErrors.HandleRead(ex, requested.Repository.DbPath);
            if (ex is not PermissionDeniedException)
                Serilog.Log.Warning(ex, "Не удалось обновить отбор по текущему периоду времени");
        }
        finally
        {
            if (ReferenceEquals(_datePeriodQueryRefresh, work)) _datePeriodQueryRefresh = null;
            if (ReferenceEquals(_datePeriodQueryCancellation, cancellation)) _datePeriodQueryCancellation = null;
        }

        async Task Refresh()
        {
            // Не отменяем долгий запрос очередным минутным тиком.
            await RecordsGrid.QueryTask.WaitAsync(cancellation.Token);
            while (!CanRefreshDatePeriodQuery()) await Task.Delay(100, cancellation.Token);
            var state = CurrentDatePeriodQuery();
            if (state is null || !ReferenceEquals(state.Repository, requested.Repository)
                || state.Collection != requested.Collection || state == _datePeriodLastQuery) return;
            await RecordsGrid.RefreshQuery(() => CanRefreshDatePeriodQuery()
                && ReferenceEquals(_repository, state.Repository) && _collectionId == state.Collection,
                cancellation.Token);
            if (!cancellation.IsCancellationRequested && ReferenceEquals(_repository, state.Repository)
                && _collectionId == state.Collection) _datePeriodLastQuery = state;
        }
    }

    private async Task RefreshDatePeriodsAsync(bool force)
    {
        if (_repository is null || !IsEffectivelyVisible || VisualRoot is null) return;
        if (_datePeriodRefreshCancellation is not null || _datePeriodRecalculating)
        {
            // Новое окно не теряется, пока прежний запрос завершается или отменяется.
            _datePeriodViewportDirty = true;
            return;
        }
        var surface = RecordsGrid.Surface;
        var columns = surface.Columns;
        var shown = new HashSet<int>();
        var frozen = Math.Min(surface.FrozenColumns, columns.Count);
        for (var column = 0; column < frozen && columns.Left(column) < surface.ViewportWidth; column++)
            if (columns[column].Tag is int fieldId) shown.Add(fieldId);
        var frozenWidth = frozen == 0 ? 0 : columns.Right(frozen - 1);
        var (firstColumn, lastColumn) = columns.VisibleRange(surface.OffsetX + frozenWidth,
            Math.Max(0, surface.ViewportWidth - frozenWidth));
        for (var column = Math.Max(frozen, firstColumn); column <= lastColumn; column++)
            if (columns[column].Tag is int fieldId) shown.Add(fieldId);
        var fields = _fields.Where(field => shown.Contains(field.Id!.Value) && field.FieldType == "date_period"
            && DatePeriodConfig.FromConfig(field.ConfigJson) is { Automatic: true, EndFieldId: null })
            .Select(field => field.Id!.Value).ToArray();
        if (fields.Length == 0 || RecordsGrid.Rows is not { } rows) return;
        var (first, last) = RecordsGrid.Surface.VisibleRows();
        var ids = Enumerable.Range(Math.Max(0, first), Math.Max(0, last - Math.Max(0, first) + 1))
            .Select(index => rows.TryGetCached(index)?.RecordId ?? 0).Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return;
        var repository = _repository;
        var collection = _collectionId;
        var state = new DatePeriodViewportState(repository, rows, collection, _datePeriodViewportVersion,
            repository.DatePeriodClock().Ticks / TimeSpan.TicksPerMinute, ids, fields);
        if (!force && _datePeriodLastViewport?.Matches(state) == true) return;
        using var cancellation = new CancellationTokenSource();
        _datePeriodRefreshCancellation = cancellation;
        try
        {
            var values = await repository.ReadDatePeriodsAsync(ids, fields, cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_repository, repository)
                || _collectionId != collection || !ReferenceEquals(RecordsGrid.Rows, rows)
                || _datePeriodViewportVersion != state.Version || !IsEffectivelyVisible
                || VisualRoot is null || _datePeriodRecalculating) return;
            foreach (var (recordId, byField) in values) rows.FindCachedByRecordId(recordId)?.MergeValues(byField);
            // MergeValues и разметка могут еще раз поднять события поверхности. Совпавшая
            // сигнатура остановит этот путь до нового SQL, не образуя цикл обновления.
            _datePeriodLastViewport = state;
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
            if (_datePeriodViewportDirty) QueueDatePeriodViewportRefresh();
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
        finally
        {
            progress?.Done();
            _datePeriodRecalculating = false;
            if (_datePeriodViewportDirty) QueueDatePeriodViewportRefresh();
        }
    }
}
