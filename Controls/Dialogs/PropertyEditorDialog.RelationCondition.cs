using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SmartBase.Core.Models;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Условие выбора записей связи (<see cref="RelationCondition"/>): строки «свойство · сравнение ·
/// значение», между строками — «и». У текста значение набирается, у списка, статуса и
/// пользователя — галочками пунктов (можно несколько), у флажка значения нет. Нечитаемое условие
/// (повреждено или задано более новой версией) строками не показывается: оно хранится как есть,
/// пока его не уберут кнопкой.
/// </summary>
public partial class PropertyEditorDialog
{
    private sealed record ConditionOpItem(string Op, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed class ConditionRow
    {
        public required Control Root { get; init; }
        public required ComboBox Property { get; init; }
        public required ComboBox Op { get; init; }
        public required TextBox Text { get; init; }
        public required WrapPanel Choices { get; init; }
        public required TextBlock NoValue { get; init; }
        public IReadOnlyList<string> Restore { get; set; } = [];
        // Свойство, для которого построены галочки: при повторной постройке (сменилось
        // сравнение, перечитан список свойств) отмеченные пункты сохраняются.
        public string? ChoicesKey { get; set; }
    }

    private readonly List<ConditionRow> _conditionRows = [];
    private string? _conditionUnreadable;

    private void InitializeRelationCondition()
    {
        AddConditionBtn.Click += (_, _) => AddConditionRow(null);
        RemoveConditionBtn.Click += (_, _) =>
        {
            _conditionUnreadable = null;
            RelationConditionNote.Hide();
            UpdateConditionState();
        };
        UpdateConditionState();
    }

    // Свойства цели, которые можно поставить в условие.
    private List<RelationPropertyItem> ConditionProperties() =>
        (RelationDisplayCombo.ItemsSource as IEnumerable<RelationPropertyItem> ?? [])
        .Where(p => RelationCondition.SupportsType(RelationCondition.TypeOf(p.Key, p.FieldType))).ToList();

    private void AddConditionRow(RelationCondition? condition)
    {
        var properties = ConditionProperties();
        var property = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, Height = 34, VerticalContentAlignment = VerticalAlignment.Center,
            Background = ResourceBrush("BrushSecondaryBg", Brushes.WhiteSmoke), CornerRadius = ResourceRadius("RadiusBase", 6),
            ItemsSource = properties,
        };
        var op = new ComboBox
        {
            Width = 150, Height = 34, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
            Background = ResourceBrush("BrushSecondaryBg", Brushes.WhiteSmoke), CornerRadius = ResourceRadius("RadiusBase", 6),
        };
        var delete = new Button
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Content = BuildDeleteGlyph(),
        };
        ToolTip.SetTip(delete, "Убрать условие");
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        Grid.SetColumn(op, 1);
        Grid.SetColumn(delete, 2);
        head.Children.Add(property);
        head.Children.Add(op);
        head.Children.Add(delete);
        var text = new TextBox
        {
            Height = 34, VerticalContentAlignment = VerticalAlignment.Center, Watermark = "Значение",
            MaxLength = RelationCondition.MaxValueLength,
        };
        var choices = new WrapPanel { Orientation = Orientation.Horizontal };
        var noValue = new TextBlock
        {
            Text = "Значение не нужно", Foreground = ResourceBrush("BrushSecondaryText", Brushes.Gray),
        };
        var root = new StackPanel { Spacing = 6 };
        if (_conditionRows.Count > 0)
            root.Children.Add(new TextBlock { Text = "и", Foreground = ResourceBrush("BrushSecondaryText", Brushes.Gray) });
        root.Children.Add(head);
        root.Children.Add(text);
        root.Children.Add(choices);
        root.Children.Add(noValue);
        var row = new ConditionRow
        {
            Root = root, Property = property, Op = op, Text = text, Choices = choices, NoValue = noValue,
            Restore = condition?.Values ?? [],
        };
        _conditionRows.Add(row);
        RelationConditionList.Children.Add(root);

        property.SelectionChanged += (_, _) => FillConditionOps(row, null);
        op.SelectionChanged += (_, _) => UpdateConditionValue(row);
        delete.Click += (_, _) =>
        {
            _conditionRows.Remove(row);
            RelationConditionList.Children.Remove(root);
            // Союз «и» у новой первой строки лишний.
            if (_conditionRows.Count > 0 && _conditionRows[0].Root is StackPanel { Children: [TextBlock and, ..] } first)
                first.Children.Remove(and);
            UpdateConditionState();
        };

        var key = condition?.Field ?? properties.FirstOrDefault()?.Key;
        property.SelectedIndex = properties.FindIndex(p => p.Key == key);
        FillConditionOps(row, condition?.Op);
        if (condition is not null && !RelationCondition.IsChoice(Type(row))) text.Text = condition.Values.FirstOrDefault() ?? "";
        UpdateConditionState();
        if (condition is null) property.Focus();
    }

    private static string Type(ConditionRow row) =>
        row.Property.SelectedItem is RelationPropertyItem p ? RelationCondition.TypeOf(p.Key, p.FieldType) : "";

    private void FillConditionOps(ConditionRow row, string? selected)
    {
        var type = Type(row);
        var ops = RelationCondition.OpsFor(type).Select(o => new ConditionOpItem(o, RelationCondition.Label(o, type))).ToList();
        var keep = selected ?? (row.Op.SelectedItem as ConditionOpItem)?.Op;
        row.Op.ItemsSource = ops;
        row.Op.SelectedIndex = Math.Max(0, ops.FindIndex(o => o.Op == keep));
        FillConditionChoices(row);
        UpdateConditionValue(row);
    }

    // Пункты списка, статуса или пользователей цели — галочками. Сохраненный пункт, которого уже
    // нет в списке, остается отмеченным со своей пометкой: записи с этим значением по-прежнему
    // подходят.
    private void FillConditionChoices(ConditionRow row)
    {
        var p = row.Property.SelectedItem as RelationPropertyItem;
        IReadOnlyList<string> restore = row.Restore.Count > 0 ? row.Restore
            : p is not null && p.Key == row.ChoicesKey ? CheckedChoices(row) : [];
        row.Choices.Children.Clear();
        row.ChoicesKey = null;
        if (p is null) return;
        row.Restore = [];
        if (!RelationCondition.IsChoice(p.FieldType)) return;
        var dbId = (RelationDbCombo.SelectedItem as RelationDbItem)?.Id ?? _currentDbId;
        var options = p.FieldType == "user"
            ? RepoForDb(dbId)?.GetUserOptions() ?? []
            : ParseOptions(p.ConfigJson).ToList();
        row.ChoicesKey = p.Key;
        foreach (var value in options.Concat(restore.Where(v => !options.Contains(v))).Distinct())
            row.Choices.Children.Add(new CheckBox
            {
                Content = options.Contains(value) ? value : $"{value} (нет в списке)",
                Tag = value,
                IsChecked = restore.Contains(value),
                Margin = new Thickness(0, 0, 12, 0),
            });
    }

    private static List<string> CheckedChoices(ConditionRow row) =>
        row.Choices.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag!).ToList();

    private void UpdateConditionValue(ConditionRow row)
    {
        var needs = row.Op.SelectedItem is ConditionOpItem item && RelationCondition.NeedsValues(item.Op);
        var choice = RelationCondition.IsChoice(Type(row));
        row.Text.IsVisible = needs && !choice;
        row.Choices.IsVisible = needs && choice;
        row.NoValue.IsVisible = !needs && row.Property.SelectedItem is not null && Type(row) != "checkbox";
    }

    private void UpdateConditionState()
    {
        var unreadable = _conditionUnreadable is not null;
        RelationConditionList.IsVisible = !unreadable;
        RelationConditionEmpty.IsVisible = !unreadable && _conditionRows.Count == 0;
        AddConditionBtn.IsVisible = !unreadable && _conditionRows.Count < RelationCondition.MaxConditions;
        RemoveConditionBtn.IsVisible = unreadable;
    }

    // Список свойств цели сменился. Другая коллекция или база — строки сбрасываются (их свойства
    // не отсюда); та же — строки строятся заново по новому списку с тем же выбором: смена списка
    // у ComboBox сбросила бы сравнение и отмеченные пункты.
    private void RefreshRelationConditionRows(bool keepSelection)
    {
        var current = keepSelection ? _conditionRows.Select(row => new RelationCondition(
            (row.Property.SelectedItem as RelationPropertyItem)?.Key ?? "",
            (row.Op.SelectedItem as ConditionOpItem)?.Op ?? "",
            RelationCondition.IsChoice(Type(row)) ? CheckedChoices(row)
                : (row.Text.Text ?? "") is { Length: > 0 } text ? [text] : [])).ToList() : [];
        _conditionRows.Clear();
        RelationConditionList.Children.Clear();
        foreach (var condition in current) AddConditionRow(condition);
        UpdateConditionState();
    }

    private void RestoreRelationCondition(RelationConfig cfg)
    {
        _conditionRows.Clear();
        RelationConditionList.Children.Clear();
        _conditionUnreadable = cfg.WhereUnreadable;
        if (_conditionUnreadable is not null)
        {
            RelationConditionNote.Kind = Widgets.NoticeBanner.BannerKind.Info;
            RelationConditionNote.Show("Условие выбора задано более новой версией программы или повреждено. " +
                                       "Пока его не уберут, новые связи не выбираются.");
        }
        foreach (var condition in cfg.Where ?? []) AddConditionRow(condition);
        UpdateConditionState();
    }

    // Новое или измененное условие, под которое сейчас не подходит ни одна запись цели: выбрать
    // будет нечего — спрашиваем, не ошибка ли это.
    private async Task<bool> ConfirmEmptyRelationCondition(string configJson)
    {
        if (RelationConfig.FromConfig(configJson) is not { Where.Count: > 0 } cfg) return true;
        var before = RelationConfig.FromConfig(_editField?.ConfigJson);
        if (before is not null && before.TargetCollectionId == cfg.TargetCollectionId && before.TargetDbId == cfg.TargetDbId
            && (before.Where ?? []).SequenceEqual(cfg.Where!)) return true;
        var dbId = cfg.TargetDbId == 0 ? _currentDbId : cfg.TargetDbId;
        if (RepoForDb(dbId) is not { } target) return true;
        try
        {
            if (target.SearchRelationRecords(cfg.TargetCollectionId, RelationConfig.TitleField, null, take: 1, condition: cfg).Count > 0)
                return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException) { return true; }
        var name = (RelationTargetCombo.SelectedItem as TargetCollectionItem)?.Name ?? "";
        return await new ConfirmDialog(
            $"Сейчас ни одна запись коллекции «{name}» не подходит под условие выбора — выбрать будет нечего. Сохранить так?",
            "Сохранить", "Вернуться").ShowDialog<bool>(this);
    }

    // Условие из строк окна; null — строка заполнена не до конца (ошибка показана).
    private (IReadOnlyList<RelationCondition>? Where, string? Unreadable)? BuildRelationCondition()
    {
        if (_conditionUnreadable is not null) return (null, _conditionUnreadable);
        var where = new List<RelationCondition>();
        foreach (var row in _conditionRows)
        {
            if (row.Property.SelectedItem is not RelationPropertyItem property || row.Op.SelectedItem is not ConditionOpItem op)
            {
                ShowError("Выберите свойство в каждом условии выбора");
                return null;
            }
            IReadOnlyList<string> values = [];
            if (RelationCondition.NeedsValues(op.Op))
            {
                values = RelationCondition.IsChoice(Type(row))
                    ? row.Choices.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag!).ToList()
                    : (row.Text.Text ?? "").Trim() is { Length: > 0 } text ? [text] : [];
                if (values.Count == 0)
                {
                    ShowError($"Укажите значение в условии «{property.Name}»");
                    return null;
                }
            }
            where.Add(new RelationCondition(property.Key, op.Op, values));
        }
        return (where.Count == 0 ? null : where, null);
    }
}
