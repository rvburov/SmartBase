using Avalonia.Controls;
using SmartBase.Controls.Dialogs;
using SmartBase.Core.Models;
using SmartBase.Utils;

namespace SmartBase.Views.ConstructorDb;

public partial class RecordPageView
{
    private bool _datePeriodRecalculating;

    private async Task RecalculateDatePeriodsAsync(IReadOnlyList<int> fieldIds)
    {
        if (_repo is null || _vm is null || _datePeriodRecalculating) return;
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
        catch (Exception ex)
        {
            if (!DbErrors.Handle(ex, repository.DbPath) && VisualRoot is Window window)
                await ConfirmDialog.Info(ex.Message).ShowDialog(window);
        }
        finally { _datePeriodRecalculating = false; }
    }
}
