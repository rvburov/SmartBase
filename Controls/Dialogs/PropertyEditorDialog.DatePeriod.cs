using Avalonia.Controls;
using SmartBase.Core.Models;

namespace SmartBase.Controls.Dialogs;

public partial class PropertyEditorDialog
{
    public sealed record DatePeriodFieldItem(int Id, string Name, bool Missing = false)
    {
        public override string ToString() => Name;
    }

    private List<DatePeriodFieldItem> _datePeriodSources = [];

    private void InitializeDatePeriod()
    {
        _datePeriodSources = _repository.GetFields(_collectionId)
            .Where(field => field.FieldType == "date" && field.Id is not null && field.Id != _editFieldId)
            .Select(field => new DatePeriodFieldItem(field.Id!.Value, field.Name)).ToList();
        _types = FieldTypes.Select(type => type.Type != "date_period" ? type : new FieldTypeItem
        {
            Type = type.Type, Label = type.Label, Icon = type.Icon, StartsGroup = type.StartsGroup,
            IsEnabled = _datePeriodSources.Count > 0,
            Hint = _datePeriodSources.Count > 0 ? null : "Сначала добавьте в коллекцию свойство типа «Дата».",
        }).ToArray();
        TypeCombo.ItemsSource = _types;
        // Контейнеры выпадающего списка могут переиспользоваться: доступность ставится
        // при каждой подготовке, как у разрешенного диапазона TimeField.
        TypeCombo.ContainerPrepared += (_, e) =>
        {
            if (e.Container is not ComboBoxItem item || e.Index < 0 || e.Index >= _types.Length) return;
            item.IsEnabled = _types[e.Index].IsEnabled;
            ToolTip.SetTip(item, _types[e.Index].Hint);
        };
        DatePeriodStartCombo.ItemsSource = _datePeriodSources;
        DatePeriodEndCombo.ItemsSource = _datePeriodSources;
        DatePeriodStartCombo.SelectedIndex = _datePeriodSources.Count == 1 ? 0 : -1;
        DatePeriodEndModeCombo.SelectedIndex = 1;
        DatePeriodEndModeCombo.SelectionChanged += (_, _) => { UpdateDatePeriodVisibility(); HideError(); };
        DatePeriodAutomaticToggle.IsCheckedChanged += (_, _) => { UpdateDatePeriodVisibility(); HideError(); };
        DatePeriodStartCombo.SelectionChanged += (_, _) => HideError();
        DatePeriodEndCombo.SelectionChanged += (_, _) => HideError();
        DatePeriodDayStart.TextChanged += (_, _) => HideError();
        DatePeriodDayEnd.TextChanged += (_, _) => HideError();
        foreach (var day in DatePeriodWeekdays.Children.OfType<CheckBox>())
            day.IsCheckedChanged += (_, _) => HideError();
        UpdateDatePeriodVisibility();
    }

    private void UpdateDatePeriodVisibility()
    {
        DatePeriodEndCombo.IsVisible = DatePeriodEndModeCombo.SelectedIndex == 1;
        DatePeriodModeHint.Text = DatePeriodAutomaticToggle.IsChecked == true
            ? "Значение рассчитывается по выбранным датам и обновляется при просмотре."
            : "Значение фиксируется при создании записи. Для существующих записей используйте «Пересчитать» в меню записи или свойства.";
    }

    private void RestoreDatePeriodConfig(string? json)
    {
        if (DatePeriodConfig.FromConfig(json) is not { } config) return;
        var choices = _datePeriodSources.ToList();
        DatePeriodFieldItem Source(int id)
        {
            if (choices.FirstOrDefault(field => field.Id == id) is { } field) return field;
            var missing = new DatePeriodFieldItem(id, "Свойство даты удалено или изменило тип", Missing: true);
            choices.Add(missing);
            return missing;
        }
        var start = Source(config.StartFieldId);
        var end = config.EndFieldId is int endId ? Source(endId) : null;
        DatePeriodStartCombo.ItemsSource = choices;
        DatePeriodEndCombo.ItemsSource = choices;
        DatePeriodStartCombo.SelectedItem = start;
        DatePeriodEndModeCombo.SelectedIndex = end is null ? 0 : 1;
        DatePeriodEndCombo.SelectedItem = end;
        DatePeriodAutomaticToggle.IsChecked = config.Automatic;
        DatePeriodDayStart.Text = config.DayStart;
        DatePeriodDayEnd.Text = config.DayEnd;
        var days = DatePeriodWeekdays.Children.OfType<CheckBox>().ToArray();
        for (var i = 0; i < days.Length; i++) days[i].IsChecked = (config.Weekdays & (1 << i)) != 0;
        UpdateDatePeriodVisibility();
    }

    private string? BuildDatePeriodConfigJson()
    {
        if (DatePeriodStartCombo.SelectedItem is not DatePeriodFieldItem { Missing: false } start)
        {
            ShowError("Выберите свойство даты, от которого считать время.");
            return null;
        }
        int? endId = null;
        if (DatePeriodEndModeCombo.SelectedIndex is not (0 or 1))
        {
            ShowError("Выберите, до какой даты считать время.");
            return null;
        }
        if (DatePeriodEndModeCombo.SelectedIndex == 1)
        {
            if (DatePeriodEndCombo.SelectedItem is not DatePeriodFieldItem { Missing: false } end)
            {
                ShowError("Выберите свойство даты окончания.");
                return null;
            }
            endId = end.Id;
        }
        var mask = 0;
        var days = DatePeriodWeekdays.Children.OfType<CheckBox>().ToArray();
        for (var i = 0; i < days.Length; i++) if (days[i].IsChecked == true) mask |= 1 << i;
        if (mask == 0)
        {
            ShowError("Выберите хотя бы один день недели.");
            return null;
        }
        var config = new DatePeriodConfig(start.Id, endId, DatePeriodAutomaticToggle.IsChecked == true,
            mask, DatePeriodDayStart.Text?.Trim() ?? "", DatePeriodDayEnd.Text?.Trim() ?? "");
        if (!config.IsValid)
        {
            ShowError("Укажите время в формате чч:мм. Конец должен быть позже начала; 24:00 допустимо только для конца дня.");
            return null;
        }
        return config.ToConfigJson(_editField?.FieldType == "date_period" ? _editField.ConfigJson : null);
    }
}
