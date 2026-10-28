using Avalonia.Controls;
using Avalonia.Threading;
using SmartBase.Controls.Dialogs;
using SmartBase.Core.Models;
using SmartBase.Utils;

namespace SmartBase.Views.ConstructorDb;

public partial class RecordPageView
{
    private readonly DispatcherTimer _datePeriodTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private CancellationTokenSource? _datePeriodRefreshCancellation;
    private bool _datePeriodRecalculating;

    private void InitializeDatePeriodRuntime()
    {
        _datePeriodTimer.Tick += async (_, _) => await RefreshDatePeriodsForTimeAsync();
        AttachedToVisualTree += (_, _) => _datePeriodTimer.Start();
        DetachedFromVisualTree += (_, _) =>
        {
            _datePeriodTimer.Stop();
            _datePeriodRefreshCancellation?.Cancel();
        };
    }

    private async Task RefreshDatePeriodsForTimeAsync()
    {
        if (_repo is null || _vm is null || !IsEffectivelyVisible || VisualRoot is null
            || _datePeriodRefreshCancellation is not null || _datePeriodRecalculating) return;
        var fields = _vm.Properties.Where(row => row.Field.FieldType == "date_period"
            && DatePeriodConfig.FromConfig(row.Field.ConfigJson) is { Automatic: true, EndFieldId: null })
            .Select(row => row.Field.Id!.Value).ToArray();
        if (fields.Length == 0) return;
        var repository = _repo;
        var model = _vm;
        var stamp = DataStamp;
        using var cancellation = new CancellationTokenSource();
        _datePeriodRefreshCancellation = cancellation;
        try
        {
            var values = await repository.ReadDatePeriodsAsync([model.RecordId], fields, cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_repo, repository)
                || !ReferenceEquals(_vm, model) || DataStamp != stamp || !IsEffectivelyVisible
                || VisualRoot is null || _datePeriodRecalculating) return;
            if (values.GetValueOrDefault(model.RecordId) is not { } byField) return;
            foreach (var (fieldId, value) in byField)
                if (_rowsByField.TryGetValue(fieldId, out var row)) row.Value = value;
            PropertiesHost.RefreshValues();
            var index = PropertiesHost.EditingIndex;
            if (index >= 0 && index < _cards.Count
                && _cards[index].Tag is SmartBase.ViewModels.ConstructorDb.PropertyRowVm current
                && byField.ContainsKey(current.Field.Id!.Value)
                && PropertiesHost.CurrentEditor is TextBox { IsReadOnly: true } editor)
                editor.Text = FieldTypeInfo.FormatComputedDisplay(current.Value, current.Field.FieldType);
            SchedulePropertyFilter();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            DbErrors.HandleRead(ex, repository.DbPath);
            if (ex is not PermissionDeniedException)
                Serilog.Log.Warning(ex, "Не удалось обновить периоды времени в открытой записи");
        }
        finally
        {
            if (ReferenceEquals(_datePeriodRefreshCancellation, cancellation)) _datePeriodRefreshCancellation = null;
        }
    }

    private async Task RecalculateDatePeriodsAsync(IReadOnlyList<int> fieldIds)
    {
        if (_repo is null || _vm is null || _datePeriodRecalculating) return;
        _datePeriodRefreshCancellation?.Cancel();
        var selected = _vm.Properties.Where(row => fieldIds.Contains(row.Field.Id!.Value)
            && row.Field.FieldType == "date_period"
            && DatePeriodConfig.FromConfig(row.Field.ConfigJson) is { Automatic: false })
            .Select(row => row.Field.Id!.Value).ToArray();
        if (selected.Length == 0) return;
        var repository = _repo;
        var recordId = _vm.RecordId;
        _datePeriodRecalculating = true;
        try
        {
            await repository.RecalculateDatePeriodsAsync([recordId], selected);
            if (!ReferenceEquals(_repo, repository) || _vm?.RecordId != recordId) return;
            ReloadFromDb();
            NotifyRecordChanged();
        }
        catch (PermissionDeniedException)
        {
            PermissionNotice.Run(this, false, PermAction.EditRecord, () => { });
        }
        catch (Exception ex)
        {
            if (!DbErrors.Handle(ex, repository.DbPath) && VisualRoot is Window window)
                await ConfirmDialog.Info(ex.Message).ShowDialog(window);
        }
        finally { _datePeriodRecalculating = false; }
    }
}
