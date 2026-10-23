using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SmartBase.Core.Models;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Пары свойств автоподбора (<see cref="RelationAutoRule"/>): первая пара — в основной сетке
/// (RelationAutoSourceCombo / RelationAutoTargetCombo), остальные — строками под ней, до
/// <see cref="RelationAutoRule.MaxPairs"/>. Флажок «Не различать регистр и пробелы по краям»
/// действует на все правило; без текстовых пар он неактивен и сохраняется снятым — правило из
/// одной пары остается понятным прежней версии программы. Правило более новой версии хранится
/// как есть; потерянное или поврежденное задается заново.
/// </summary>
public partial class PropertyEditorDialog
{
    private sealed class AutoPairRow
    {
        public required Control Root { get; init; }
        public required ComboBox Source { get; init; }
        public required ComboBox Target { get; init; }
    }

    private readonly List<AutoPairRow> _autoExtra = [];
    // Правило более новой версии: окно его не меняет и сохраняет как есть.
    private RelationAutoRule? _keptAutoRule;

    private void InitializeRelationAuto()
    {
        AddAutoPairBtn.Click += (_, _) => AddAutoPairRow(null).Source.Focus();
        RelationAutoSourceCombo.SelectionChanged += (_, _) => UpdateAutoFoldState();
        RelationAutoTargetCombo.SelectionChanged += (_, _) => UpdateAutoFoldState();
        UpdateAutoPairsState();
    }

    private AutoPairRow AddAutoPairRow(RelationAutoPair? pair)
    {
        ComboBox Combo(object? items) => new()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, Height = 34, VerticalContentAlignment = VerticalAlignment.Center,
            Background = ResourceBrush("BrushSecondaryBg", Brushes.WhiteSmoke), CornerRadius = ResourceRadius("RadiusBase", 6),
            ItemsSource = items as System.Collections.IEnumerable,
        };
        var source = Combo(RelationAutoSourceCombo.ItemsSource);
        var target = Combo(RelationAutoTargetCombo.ItemsSource);
        var delete = new Button
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
            Content = BuildDeleteGlyph(),
        };
        ToolTip.SetTip(delete, "Убрать пару");
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*,36") };
        Grid.SetColumn(target, 2);
        Grid.SetColumn(delete, 3);
        root.Children.Add(source);
        root.Children.Add(target);
        root.Children.Add(delete);
        var row = new AutoPairRow { Root = root, Source = source, Target = target };
        _autoExtra.Add(row);
        RelationAutoExtra.Children.Add(root);

        if (pair is not null)
        {
            SelectRelationProperty(source, pair.SourceField);
            SelectRelationProperty(target, pair.TargetField);
        }
        else
        {
            // Новая пара: первое еще не взятое свойство этой коллекции и свойство цели с тем же
            // именем (регистр не важен), иначе первое не взятое.
            var used = AutoPairs().ToList();
            var sources = (RelationAutoSourceCombo.ItemsSource as IEnumerable<RelationPropertyItem> ?? []).ToList();
            var targets = (RelationAutoTargetCombo.ItemsSource as IEnumerable<RelationPropertyItem> ?? []).ToList();
            var first = sources.FirstOrDefault(p => used.All(u => u.Source?.Key != p.Key));
            source.SelectedItem = first;
            target.SelectedItem = targets.FirstOrDefault(p => first is not null
                                      && string.Equals(p.Name, first.Name, StringComparison.OrdinalIgnoreCase))
                                  ?? targets.FirstOrDefault(p => used.All(u => u.Target?.Key != p.Key));
        }
        source.SelectionChanged += (_, _) => UpdateAutoFoldState();
        target.SelectionChanged += (_, _) => UpdateAutoFoldState();
        delete.Click += (_, _) =>
        {
            _autoExtra.Remove(row);
            RelationAutoExtra.Children.Remove(root);
            UpdateAutoPairsState();
        };
        UpdateAutoPairsState();
        return row;
    }

    private IEnumerable<(RelationPropertyItem? Source, RelationPropertyItem? Target)> AutoPairs() =>
        new[] { (RelationAutoSourceCombo.SelectedItem as RelationPropertyItem, RelationAutoTargetCombo.SelectedItem as RelationPropertyItem) }
            .Concat(_autoExtra.Select(r => (r.Source.SelectedItem as RelationPropertyItem, r.Target.SelectedItem as RelationPropertyItem)));

    private void UpdateAutoPairsState()
    {
        var kept = _keptAutoRule is not null;
        AddAutoPairBtn.IsVisible = !kept && _autoExtra.Count + 1 < RelationAutoRule.MaxPairs;
        RelationAutoSourceCombo.IsEnabled = RelationAutoTargetCombo.IsEnabled = !kept;
        UpdateAutoFoldState();
    }

    // Флажок работает только на текст (и пункты мультисписка, и текст из связанных записей).
    private void UpdateAutoFoldState()
    {
        static bool Text(RelationPropertyItem? p) => p is not null
            && RelationAutoMatch.Family(p.FieldType, p.ConfigJson) is "text" or "multiselect" or "lookup";
        RelationAutoFoldCheck.IsEnabled = _keptAutoRule is null && AutoPairs().Any(p => Text(p.Source) || Text(p.Target));
    }

    // Список свойств сменился — строки пар берут его, сохраняя выбор, где можно.
    private void RefreshRelationAutoRows()
    {
        foreach (var row in _autoExtra)
        {
            var source = (row.Source.SelectedItem as RelationPropertyItem)?.Key;
            var target = (row.Target.SelectedItem as RelationPropertyItem)?.Key;
            row.Source.ItemsSource = RelationAutoSourceCombo.ItemsSource;
            row.Target.ItemsSource = RelationAutoTargetCombo.ItemsSource;
            SelectRelationProperty(row.Source, source ?? RelationConfig.TitleField);
            SelectRelationProperty(row.Target, target ?? RelationConfig.TitleField);
        }
        UpdateAutoFoldState();
    }

    private void RestoreRelationAuto(RelationAutoRule rule)
    {
        _keptAutoRule = null;
        RelationAutoNote.Hide();
        if (rule.IsReady)
        {
            SelectRelationProperty(RelationAutoSourceCombo, rule.Pairs[0].SourceField);
            SelectRelationProperty(RelationAutoTargetCombo, rule.Pairs[0].TargetField);
            foreach (var pair in rule.Pairs.Skip(1)) AddAutoPairRow(pair);
            RelationAutoFoldCheck.IsChecked = rule.Fold;
        }
        else
        {
            RelationAutoNote.Kind = Widgets.NoticeBanner.BannerKind.Info;
            RelationAutoNote.Show(rule.State switch
            {
                RelationAutoState.Lost => "Пары свойств автоподбора потеряны: связь копировали или меняли в прежней версии программы. " +
                                          "Выберите пары заново. До сохранения связи не меняются.",
                RelationAutoState.Newer => "Правило автоподбора создано более новой версией программы. Его можно оставить как есть " +
                                           "или выбрать «Ручной выбор»; чтобы изменить пары, обновите программу.",
                _ => "Настройка автоподбора повреждена. Выберите пары заново. До сохранения связи не меняются.",
            });
            if (rule.State == RelationAutoState.Newer) _keptAutoRule = rule;
            // Пары не известны: списки пустые (сохранение без выбора попросит выбрать пары), флажок
            // не изображает свертку, которой, может быть, нет.
            RelationAutoSourceCombo.SelectedIndex = RelationAutoTargetCombo.SelectedIndex = -1;
            RelationAutoFoldCheck.IsChecked = false;
        }
        UpdateAutoPairsState();
    }

    private void ForgetKeptAutoRule()
    {
        _keptAutoRule = null;
        RelationAutoNote.Kind = Widgets.NoticeBanner.BannerKind.Info;
        RelationAutoNote.Show("Целевая коллекция изменилась: правило автоподбора более новой версии к ней не подходит. Выберите пары заново.");
        RelationAutoSourceCombo.SelectedIndex = RelationAutoTargetCombo.SelectedIndex = -1;
        UpdateAutoPairsState();
    }

    // Правило из строк окна; null — пара заполнена не до конца или повторяется (ошибка показана).
    private RelationAutoRule? BuildRelationAutoRule()
    {
        if (_keptAutoRule is not null) return _keptAutoRule;
        var pairs = AutoPairs().ToList();
        var result = new List<RelationAutoPair>();
        for (var i = 0; i < pairs.Count; i++)
        {
            if (pairs[i].Source is not { } source || pairs[i].Target is not { } target)
            {
                ShowError(pairs.Count == 1 ? "Выберите свойства для автоматического сопоставления" : $"Выберите оба свойства в паре {i + 1}.");
                return null;
            }
            var pair = new RelationAutoPair(source.Key, target.Key);
            if (result.IndexOf(pair) is var same and >= 0)
            {
                ShowError($"Пара {i + 1} повторяет пару {same + 1}.");
                return null;
            }
            result.Add(pair);
        }
        return new RelationAutoRule(result, RelationAutoFoldCheck.IsEnabled && RelationAutoFoldCheck.IsChecked == true);
    }
}
