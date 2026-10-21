using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SmartBase.Core.Models;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Показ и поиск связи по нескольким свойствам цели. Первое свойство — в основном списке
/// (RelationDisplayCombo / RelationSearchCombo), остальные — строками под ним. В настройке они
/// хранятся составным ключом (<see cref="RelationConfig.KeySeparator"/>).
/// </summary>
public partial class PropertyEditorDialog
{
    private readonly List<ComboBox> _displayExtra = [];
    private readonly List<ComboBox> _searchExtra = [];

    private void InitializeRelationKeys()
    {
        AddDisplayBtn.Click += (_, _) => AddKeyRow(RelationDisplayExtra, _displayExtra, AddDisplayBtn,
            RelationConfig.MaxDisplayFields, null).Focus();
        AddSearchBtn.Click += (_, _) => AddKeyRow(RelationSearchExtra, _searchExtra, AddSearchBtn,
            RelationConfig.MaxSearchFields, null).Focus();
    }

    private ComboBox AddKeyRow(StackPanel host, List<ComboBox> rows, Button add, int max, string? key)
    {
        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, Height = 34, VerticalContentAlignment = VerticalAlignment.Center,
            Background = ResourceBrush("BrushSecondaryBg", Brushes.WhiteSmoke), CornerRadius = ResourceRadius("RadiusBase", 6),
            ItemsSource = RelationDisplayCombo.ItemsSource,
        };
        SelectRelationProperty(combo, key ?? FirstUnusedKey(host == RelationDisplayExtra));
        var delete = new Button
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Content = BuildDeleteGlyph(),
        };
        ToolTip.SetTip(delete, "Убрать свойство");
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(delete, 1);
        row.Children.Add(combo);
        row.Children.Add(delete);
        rows.Add(combo);
        host.Children.Add(row);
        add.IsVisible = rows.Count + 1 < max;
        delete.Click += (_, _) =>
        {
            rows.Remove(combo);
            host.Children.Remove(row);
            add.IsVisible = rows.Count + 1 < max;
        };
        return combo;
    }

    // Новая строка предлагает свойство, которого еще нет в показе (поиске).
    private string FirstUnusedKey(bool display)
    {
        var used = SelectedKeys(display ? RelationDisplayCombo : RelationSearchCombo, display ? _displayExtra : _searchExtra);
        return (RelationDisplayCombo.ItemsSource as IEnumerable<RelationPropertyItem>)?
            .Select(p => p.Key).FirstOrDefault(k => !used.Contains(k)) ?? RelationConfig.TitleField;
    }

    private static List<string> SelectedKeys(ComboBox primary, List<ComboBox> extra) =>
        new[] { primary }.Concat(extra).Select(c => (c.SelectedItem as RelationPropertyItem)?.Key)
            .OfType<string>().Distinct(StringComparer.Ordinal).ToList();

    // Список свойств цели сменился — дополнительные строки берут его, сохраняя выбор, где можно.
    private void RefreshRelationKeyRows()
    {
        foreach (var combo in _displayExtra.Concat(_searchExtra))
        {
            var key = (combo.SelectedItem as RelationPropertyItem)?.Key;
            combo.ItemsSource = RelationDisplayCombo.ItemsSource;
            SelectRelationProperty(combo, key ?? RelationConfig.TitleField);
        }
    }

    private void RestoreRelationKeys(RelationConfig cfg)
    {
        SelectRelationProperty(RelationDisplayCombo, cfg.DisplayKeys[0]);
        foreach (var key in cfg.DisplayKeys.Skip(1))
            AddKeyRow(RelationDisplayExtra, _displayExtra, AddDisplayBtn, RelationConfig.MaxDisplayFields, key);
        SelectRelationProperty(RelationSearchCombo, cfg.SearchKeys[0]);
        foreach (var key in cfg.SearchKeys.Skip(1))
            AddKeyRow(RelationSearchExtra, _searchExtra, AddSearchBtn, RelationConfig.MaxSearchFields, key);
    }

    // Составные ключи показа и поиска; null — не выбрано основное свойство.
    private (string Display, string Search)? BuildRelationKeys()
    {
        if (RelationDisplayCombo.SelectedItem is null || RelationSearchCombo.SelectedItem is null) return null;
        return (RelationConfig.JoinKeys(SelectedKeys(RelationDisplayCombo, _displayExtra)),
                RelationConfig.JoinKeys(SelectedKeys(RelationSearchCombo, _searchExtra)));
    }
}
