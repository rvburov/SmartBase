using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Блок «Данные из связанных записей» окна связи. Строка — отдельный столбец только для чтения:
/// значения свойства связанных записей (lookup) или итог по ним (rollup). Отдельных типов в списке
/// нет: такие столбцы живут только вместе со своей связью и настраиваются здесь же.
/// </summary>
public partial class PropertyEditorDialog
{
    private sealed record RelationDataKind(string Aggregate, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed class RelationDataRow
    {
        public int? FieldId { get; init; }
        public required Border Root { get; init; }
        public required TextBox Name { get; init; }
        public required ComboBox Property { get; init; }
        public required ComboBox Kind { get; init; }
        // Имя вводили руками — подставлять автоимя больше нельзя.
        public bool NameEdited { get; set; }
        public string AutoName { get; set; } = "";
    }

    private readonly List<RelationDataRow> _relationData = [];
    // Столбец данных, по которому открыли окно, — его строка подсвечивается и получает фокус.
    private int? _focusDataField;

    /// <summary>Поле, сохраненное окном (связь — вместе со своими столбцами данных).</summary>
    public int? SavedFieldId { get; private set; }

    /// <summary>Столбцы данных связи после сохранения, в порядке строк окна.</summary>
    public IReadOnlyList<int> SavedDataFieldIds { get; private set; } = [];

    private void InitializeRelationData()
    {
        AddRelationDataBtn.Click += (_, _) =>
        {
            var row = AddRelationDataRow(null, RelationConfig.TitleField, "lookup", null);
            row.Property.Focus();
        };
        NameBox.TextChanged += (_, _) => { foreach (var row in _relationData) UpdateAutoName(row); };
    }

    // Существующие столбцы данных связи — в строки (в порядке создания).
    private void RestoreRelationData(int relationFieldId)
    {
        foreach (var field in _repository.GetRelationDependentFields(relationFieldId))
            if (RelationComputedConfig.FromConfig(field.ConfigJson) is { } config)
                AddRelationDataRow(field.Id, config.TargetField, config.Aggregate, field.Name);
        if (_focusDataField is int focus && _relationData.FirstOrDefault(r => r.FieldId == focus) is { } target)
        {
            target.Root.BorderBrush = ResourceBrush("BrushAccent", Brushes.DodgerBlue);
            Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                target.Root.BringIntoView();
                target.Name.Focus();
            }, DispatcherPriority.Background);
        }
    }

    private RelationDataRow AddRelationDataRow(int? fieldId, string targetField, string aggregate, string? name)
    {
        var nameBox = new TextBox
        {
            Text = name ?? "", Watermark = "Название столбца", Height = 34,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = ResourceRadius("RadiusBase", 6),
        };
        // Поля карточки — на белом, как строки «Пунктов списка» на серой подложке.
        var field = ResourceBrush("BrushBg", Brushes.White);
        nameBox.Background = field;
        var property = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Height = 34,
                                      VerticalContentAlignment = VerticalAlignment.Center, Background = field };
        var kind = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Height = 34,
                                  VerticalContentAlignment = VerticalAlignment.Center, Background = field };
        var delete = new Button
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
            Content = BuildDeleteGlyph(),
        };
        ToolTip.SetTip(delete, "Удалить столбец");

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(delete, 1);
        top.Children.Add(nameBox);
        top.Children.Add(delete);
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*") };
        Grid.SetColumn(kind, 2);
        bottom.Children.Add(property);
        bottom.Children.Add(kind);
        var root = new Border
        {
            Background = ResourceBrush("BrushSecondaryBg", Brushes.WhiteSmoke),
            BorderBrush = ResourceBrush("BrushSeparator", Brushes.LightGray),
            BorderThickness = new Thickness(1),
            CornerRadius = ResourceRadius("RadiusMedium", 8),
            Padding = new Thickness(8),
            Child = new StackPanel { Spacing = 6, Children = { top, bottom } },
        };

        var row = new RelationDataRow { FieldId = fieldId, Root = root, Name = nameBox, Property = property, Kind = kind,
                                        NameEdited = !string.IsNullOrEmpty(name) };
        _relationData.Add(row);
        RelationDataList.Children.Add(root);

        FillRelationDataProperties(row, targetField);
        FillRelationDataKinds(row, aggregate);
        UpdateAutoName(row);
        property.SelectionChanged += (_, _) =>
        {
            FillRelationDataKinds(row, (kind.SelectedItem as RelationDataKind)?.Aggregate ?? "lookup");
            UpdateAutoName(row);
            HideError();
        };
        kind.SelectionChanged += (_, _) =>
        {
            property.IsEnabled = (kind.SelectedItem as RelationDataKind)?.Aggregate != "count";
            UpdateAutoName(row);
            HideError();
        };
        // Ручной ввод снимает автоимя; очистка поля возвращает его.
        nameBox.TextChanged += (_, _) =>
        {
            if (nameBox.Text == row.AutoName) return;
            row.NameEdited = !string.IsNullOrWhiteSpace(nameBox.Text);
            HideError();
        };
        delete.Click += (_, _) =>
        {
            _relationData.Remove(row);
            RelationDataList.Children.Remove(root);
        };
        return row;
    }

    // Свойства целевой коллекции — те же, что у показа и поиска связи.
    private void FillRelationDataProperties(RelationDataRow row, string? selectKey = null)
    {
        var key = selectKey ?? (row.Property.SelectedItem as RelationPropertyItem)?.Key ?? RelationConfig.TitleField;
        row.Property.ItemsSource = RelationDisplayCombo.ItemsSource;
        SelectRelationProperty(row.Property, key);
    }

    // Что показать — по типу выбранного свойства: значения и количество есть всегда, числовые
    // итоги — у числа, самая ранняя и поздняя даты — у даты.
    private static void FillRelationDataKinds(RelationDataRow row, string keep)
    {
        var type = (row.Property.SelectedItem as RelationPropertyItem)?.FieldType ?? "text";
        var kinds = new List<RelationDataKind> { new("lookup", "Значения"), new("count", "Количество") };
        if (type is "number" or "rollup" or "date_period")
            kinds.AddRange(new[] { "sum", "avg", "min", "max" }.Select(a => new RelationDataKind(a, RelationComputedConfig.AggregateLabel(a))));
        if (FieldTypeInfo.IsDateLike(type))
            kinds.AddRange(new[] { "earliest", "latest" }.Select(a => new RelationDataKind(a, RelationComputedConfig.AggregateLabel(a))));
        row.Kind.ItemsSource = kinds;
        var index = kinds.FindIndex(k => k.Aggregate == keep);
        row.Kind.SelectedIndex = index >= 0 ? index : 0;
        row.Property.IsEnabled = (row.Kind.SelectedItem as RelationDataKind)?.Aggregate != "count";
    }

    // Автоимя «Связь · свойство» до первой ручной правки: столбцу сразу понятное имя.
    private void UpdateAutoName(RelationDataRow row)
    {
        var relation = string.IsNullOrWhiteSpace(NameBox.Text) ? "Связь" : NameBox.Text.Trim();
        var aggregate = (row.Kind.SelectedItem as RelationDataKind)?.Aggregate ?? "lookup";
        var property = (row.Property.SelectedItem as RelationPropertyItem)?.Name ?? "";
        row.AutoName = aggregate switch
        {
            "lookup" => $"{relation} · {property}",
            "count" => $"{relation} · количество",
            _ => $"{relation} · {RelationComputedConfig.AggregateLabel(aggregate).ToLowerInvariant()} {property}",
        };
        if (!row.NameEdited) row.Name.Text = row.AutoName;
    }

    // Список свойств цели сменился (другая коллекция или база) — строки берут новый.
    private void RefreshRelationDataProperties()
    {
        foreach (var row in _relationData)
        {
            var aggregate = (row.Kind.SelectedItem as RelationDataKind)?.Aggregate ?? "lookup";
            FillRelationDataProperties(row);
            FillRelationDataKinds(row, aggregate);
            UpdateAutoName(row);
        }
    }

    // Проверка строк до записи связи: незаполненная строка не должна оставить связь без столбца.
    private bool ValidateRelationData()
    {
        foreach (var row in _relationData)
        {
            if (string.IsNullOrWhiteSpace(row.Name.Text))
            { ShowError("Укажите название каждого столбца данных из связи"); row.Name.Focus(); return false; }
            var aggregate = (row.Kind.SelectedItem as RelationDataKind)?.Aggregate;
            if (aggregate is null || aggregate != "count" && row.Property.SelectedItem is not RelationPropertyItem)
            { ShowError("Выберите свойство связанной записи для каждого столбца"); row.Property.Focus(); return false; }
        }
        var names = _relationData.Select(r => r.Name.Text!.Trim()).ToList();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
        { ShowError("У столбцов данных из связи одинаковые названия"); return false; }
        var own = _relationData.Where(r => r.FieldId is not null).Select(r => r.FieldId!.Value).ToHashSet();
        var taken = _repository.GetFields(_collectionId)
            .Where(f => f.Id != _editFieldId && !own.Contains(f.Id ?? 0)).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        taken.Add(NameBox.Text?.Trim() ?? "");
        if (names.FirstOrDefault(taken.Contains) is { } busy)
        { ShowError($"Свойство «{busy}» уже есть в коллекции — назовите столбец иначе"); return false; }
        return true;
    }

    // Удаляемые столбцы данных называем до записи: колонка с ее шириной и цветом пропадет.
    private async Task<bool> ConfirmRelationDataRemoval()
    {
        if (_editFieldId is not int fieldId || _selectedType != "relation") return true;
        var kept = _relationData.Where(r => r.FieldId is not null).Select(r => r.FieldId!.Value).ToHashSet();
        var removed = _repository.GetRelationDependentFields(fieldId)
            .Where(f => !kept.Contains(f.Id!.Value)).Select(f => $"«{f.Name}»").ToList();
        if (removed.Count == 0) return true;
        return await new ConfirmDialog($"Удалятся столбцы данных из связи: {string.Join(", ", removed)}. Продолжить?", "Удалить")
            .ShowDialog<bool>(this);
    }

    private CornerRadius ResourceRadius(string key, double fallback) =>
        this.TryFindResource(key, out var r) && r is CornerRadius radius ? radius : new CornerRadius(fallback);

    private IReadOnlyList<RelationDataColumn> CollectRelationData() =>
        _relationData.Select(row => new RelationDataColumn(row.FieldId, row.Name.Text?.Trim() ?? "",
            (row.Property.SelectedItem as RelationPropertyItem)?.Key ?? RelationConfig.TitleField,
            (row.Kind.SelectedItem as RelationDataKind)?.Aggregate ?? "lookup")).ToList();
}
