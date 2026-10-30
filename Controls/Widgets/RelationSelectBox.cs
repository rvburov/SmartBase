using System.ComponentModel;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Utils;
using SmartBase.ViewModels.ConstructorDb;
using SmartBase.Controls.Behaviors;

namespace SmartBase.Controls.Widgets;

/// <summary>
/// Редактор поля-связи (тип «relation») в стиле «Мультисписка»: показывает названия связанных
/// записей ТЕКСТОМ через выбранный разделитель (пусто → серое «связи нет»), фон прозрачный (как у
/// ячейки таблицы), без рамки. В таблице этот контрол — только в РЕЖИМЕ РЕДАКТИРОВАНИЯ ячейки
/// (<paramref name="editing"/> = true, как чеклист «Мультисписка»): справа появляется иконка-связь
/// (та же, что в шапке колонки), по клику по НЕЙ открывается выпадающий список выбора (поиск +
/// виртуализированный чеклист записей целевой коллекции; single — выбор одной сбрасывает прочие).
/// У каждого пункта — стрелка «→» перехода к записи (<see cref="Navigate"/>). В режиме показа
/// (editing = false) — только текст, без иконки. На панели записи всегда editing = true.
///
/// Данные тянет сам по <see cref="RecordId"/> (GetLinks) — при пере-DataContext таблицы RecordId
/// переустанавливается; перечитывает связи и при повторном показе (AttachedToVisualTree — после
/// выхода из режима правки). Правки пишет сразу (SetLinks) и поднимает <see cref="Changed"/>.
/// </summary>
public sealed class RelationSelectBox : Panel
{
    private readonly ConstructorRepository _repo;
    private readonly int  _fieldId;
    private readonly RelationConfig _cfg;
    private readonly string _separator;
    private readonly bool _editing;
    // Табличный кеш связей (батч видимой страницы): при установке RecordId читаем из него, минуя
    // точечный GetLinks. null — вне таблицы (панель записи): всегда прямое чтение из БД.
    private readonly RelationLinksCache? _cache;

    private readonly List<RelationTarget> _selected = [];
    private readonly Dictionary<RelationTarget, string> _titles = new();
    private readonly AvaloniaList<RelationOptionVm> _shown    = [];
    private readonly TextBlock _valueText;
    private readonly TextBlock _placeholder;
    private readonly Panel     _valuePanel;
    private readonly Border    _box;
    private readonly Control?  _iconHit;
    private readonly Popup?    _popup;
    private TextBox?   _search;
    private TextBlock? _moreHint;
    // Строка под поиском: условие выбора записей или почему новые связи не выбираются.
    private TextBlock? _conditionHint;
    private TextBlock? _autoHint;
    // Выбранные раньше записи вне условия выбора: связь остается, снятую выбрать снова нельзя.
    private HashSet<RelationTarget> _outside = [];
    private int _recordId;
    private CancellationTokenSource? _pickerSearch;
    private int _searchGeneration;
    private bool _canSelectForList;

    /// <summary>Поднимается после изменения набора связей.</summary>
    public event EventHandler? Changed;
    /// <summary>Переход к связанной записи — (DbId цели, Id записи). DbId=0 — та же БД, &gt;0 —
    /// межбазовая цель (открыть из ее БД). Пункт «карандаш» у строки списка.</summary>
    public event EventHandler<(int DbId, int RecordId)>? Navigate;

    public RelationSelectBox(ConstructorRepository repo, int fieldId, RelationConfig cfg,
                             bool editing = false, double contentLeftPadding = 0,
                             RelationLinksCache? cache = null)
    {
        _repo      = repo;
        _fieldId   = fieldId;
        _cfg       = cfg;
        _separator = cfg.SeparatorString;
        _editing   = editing;
        _cache     = cache;

        _valueText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Foreground        = Brush("BrushPrimaryText", Colors.Black),
        };
        _placeholder = new TextBlock
        {
            Text              = "связи нет",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground        = Brush("BrushSecondaryText", Colors.Gray),
        };

        var left = new Panel();
        left.Children.Add(_placeholder);
        left.Children.Add(_valueText);
        _valuePanel = left;

        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), VerticalAlignment = VerticalAlignment.Stretch };
        Grid.SetColumn(left, 0);
        content.Children.Add(left);

        Control? iconHit = null;
        if (editing)
        {
            // Иконка-связь справа (как в шапке колонки) — единственный триггер открытия списка.
            // Отступы — как у иконки файла (timeArrow: небольшой слева, шире справа).
            iconHit = new Border
            {
                Background        = Brushes.Transparent,
                Padding           = new Thickness(4, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Stretch,
                Child             = BuildIcon("IconFieldRelation"),
                Cursor            = new Cursor(StandardCursorType.Hand),
            };
            iconHit.PointerPressed += (_, e) => { e.Handled = true; OpenPopup(); };
            Grid.SetColumn(iconHit, 1);
            content.Children.Add(iconHit);
        }

        // Фон прозрачный (как у ячейки таблицы), без рамки — «как у текста».
        var box = new Border
        {
            Background        = Brushes.Transparent,
            Padding           = new Thickness(contentLeftPadding, 0, editing ? 0 : contentLeftPadding, 0),
            MinHeight         = 30,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child             = content,
        };
        _box     = box;
        _iconHit = iconHit;
        Children.Add(box);

        if (editing)
        {
            _popup = new Popup
            {
                PlacementTarget       = iconHit,
                Placement             = PlacementMode.BottomEdgeAlignedRight,
                IsLightDismissEnabled = true,
                Child                 = WindowZoom.WrapPopupContent(BuildChecklist()),
            };
            Children.Add(_popup);
            _popup.Closed += (_, _) => CancelSearch();
        }
        DetachedFromVisualTree += (_, _) => CancelSearch();

        // Повторный показ (после выхода из правки ячейки) — перечитать связи СВЕЖИМИ (мимо кеша),
        // чтобы текст отражал только что сделанную правку.
        AttachedToVisualTree += (_, _) => { if (_recordId > 0) ReloadSelected(preferCache: false); };

        RefreshDisplay();
    }

    /// <summary>Запись-источник. Смена → перечитать связи и перерисовать показ. Это горячий путь
    /// наполнения страницы таблицы — читаем из батч-кеша, если он прогрет.</summary>
    public int RecordId
    {
        get => _recordId;
        set { if (_recordId == value) return; _recordId = value; ReloadSelected(preferCache: true); }
    }

    /// <summary>Перечитать связи из БД (после внешних изменений).</summary>
    public void Reload() => ReloadSelected(preferCache: false);

    /// <summary>Строка таблицы наполнилась данными (уведомление "Item"). Смена записи → перечитать.
    /// Тот же RecordId, но МЕЖБАЗОВАЯ связь → тоже перечитать СВЕЖИМ: ее нет в батч-кеше, поэтому
    /// правка/фон иначе не видны (внутрибазовую держит свежей батч-кеш, лишний раз не дергаем).</summary>
    public void OnRowFilled(int recordId)
    {
        if (recordId != _recordId) { RecordId = recordId; return; }
        if (_recordId <= 0) return;
        // Тот же RecordId, но строку пере-Fill'или. Read-ячейка (editing:false) перечитывает показ,
        // чтобы ВНЕШНЕЕ изменение отразилось сразу — в т.ч. переименование ЦЕЛИ: VirtualRowList
        // пере-праймил батч-кеш свежими названиями (RefreshRelationTargets), и здесь мы их подхватываем.
        // Межбазовую читаем свежей (ее нет в кеше), внутрибазовую — из обновленного кеша (дешево).
        // Edit-ячейку (editing:true) не трогаем на тот же RecordId — там может идти правка выбора;
        // ей достаточно прежнего поведения (межбазовую освежить, внутрибазовую держит кеш/правка).
        if (_editing)
        {
            if (_cfg.IsCrossDb) ReloadSelected(preferCache: false);
        }
        else ReloadSelected(preferCache: !_cfg.IsCrossDb);
    }

    private void ReloadSelected(bool preferCache)
    {
        _selected.Clear();
        _titles.Clear();
        if (_recordId > 0)
        {
            if (preferCache && _cache is not null && _cache.TryGetTargets(_fieldId, _recordId, out var cached))
            {
                foreach (var (target, display) in cached) { _selected.Add(target); _titles[target] = display; }
            }
            else LoadSelected();
        }
        RefreshDisplay();
        if (_popup is { IsOpen: true }) RefreshList(_search?.Text);
    }

    private void LoadSelected()
    {
        foreach (var (target, display) in _repo.GetRelationLinks(_fieldId, _recordId))
        {
            _selected.Add(target);
            _titles[target] = display;
        }
    }

    /// <summary>Общий показ для рисованной карточки и редактора, с одинаковым форматированием.</summary>
    public static string DisplayText(ConstructorRepository repo, int fieldId, int recordId, RelationConfig cfg) =>
        string.Join(cfg.SeparatorString, repo.GetRelationLinks(fieldId, recordId)
            .Select(link => TitleText.FirstLine(link.Display)));

    private bool CanSelect =>
        _editing && _cfg.Auto is null && _repo.GetRecord(_recordId)?.CollectionId is not null;

    private void OpenPopup()
    {
        if (_recordId <= 0 || _popup is null) return;
        UpdateAutoHint();
        RefreshList("");
        _popup.IsOpen = true;
        _search?.Focus();
    }

    // «Подбирается автоматически: «ИНН» и «КПП».» — названия свойств этой коллекции.
    private void UpdateAutoHint()
    {
        if (_autoHint is null) return;
        _autoHint.IsVisible = _cfg.Auto is not null;
        if (_cfg.Auto is not { } auto) return;
        if (!auto.IsReady)
        {
            _autoHint.Text = ConstructorRepository.NotReadyMessage(auto);
            return;
        }
        try
        {
            var collection = _repo.GetRecord(_recordId)?.CollectionId ?? 0;
            var names = _repo.GetRelationPropertyOptions(collection).ToDictionary(o => o.Key, o => o.Name);
            var list = auto.SourceKeys.Select(k => $"«{names.GetValueOrDefault(k, "?")}»").ToList();
            var joined = list.Count == 1 ? list[0] : string.Join(", ", list.Take(list.Count - 1)) + " и " + list[^1];
            _autoHint.Text = $"Подбирается автоматически: {joined}.";
        }
        catch (Exception ex)
        {
            // База на сетевом диске недоступна: строка без названий, список открывается.
            Serilog.Log.Warning(ex, "Не удалось прочитать свойства автосвязи");
            _autoHint.Text = "Подбирается автоматически.";
        }
    }

    /// <summary>
    /// Где стоят названия — ячейка таблицы с расположением текста. «По центру» — по центру всего
    /// бокса, а не полосы левее иконки связи: полоса названий получает слева отступ на ширину иконки
    /// и становится симметричной, поэтому текст при входе в правку не сдвигается. «Справа» — у
    /// иконки: на нее названия не ложатся.
    /// </summary>
    public void SetValueAlignment(HorizontalAlignment alignment)
    {
        // Слева — прежняя растянутая надпись: текст и обрезка многоточием те же, что без настройки.
        var placed = alignment == HorizontalAlignment.Left ? HorizontalAlignment.Stretch : alignment;
        _valueText.HorizontalAlignment   = placed;
        _placeholder.HorizontalAlignment = placed;
        if (alignment != HorizontalAlignment.Center) return;

        void Fit()
        {
            var chrome = _iconHit?.Bounds.Width ?? 0;
            var margin = new Thickness(Math.Max(0, chrome + _box.Padding.Right - _box.Padding.Left), 0, 0, 0);
            if (_valuePanel.Margin != margin) _valuePanel.Margin = margin;
        }
        if (_iconHit is not null) _iconHit.SizeChanged += (_, _) => Fit();
        Fit();
    }

    /// <summary>
    /// Раскрыть выбор записей с набранным в поле поиска (правка ячейки таблицы набором): записи
    /// отбирает само поле поиска, как при наборе в нем, фокус и каретка — в конце поиска.
    /// </summary>
    public void OpenSearch(string text)
    {
        if (_recordId <= 0 || _popup is null || _search is null) return;
        UpdateAutoHint();
        if (_search.Text == text) RefreshList(text);
        else _search.Text = text;
        _popup.IsOpen = true;
        _search.Focus();
        _search.SelectionStart = _search.SelectionEnd = _search.CaretIndex = text.Length;
    }

    // Показ закрытого вида — названия через разделитель поля (как read-ячейка мультисписка).
    private void RefreshDisplay()
    {
        // Название цели показываем первой строкой (TitleText): закрытый вид — одна строка,
        // многострочное название разорвало бы ее на несколько.
        _valueText.Text        = string.Join(_separator,
            _selected.Select(id => TitleText.FirstLine(_titles.GetValueOrDefault(id, ""))));
        _valueText.IsVisible   = _selected.Count > 0;
        _placeholder.IsVisible = _selected.Count == 0;
    }

    // Иконка из реестра (Viewbox 24×24 + Path обводкой) — как «календарь» у поля даты / «скрепка»
    // у файла. Используется для иконки-связи в ячейке и «карандаша» перехода в списке.
    private static Control BuildIcon(string key)
    {
        var geo    = Application.Current!.TryFindResource(key, out var g) && g is Geometry geom ? geom : null;
        var size   = Res("IconSizeSmall", 16.0);
        var thick  = Res("IconStrokeSmall", 1.8);
        var stroke = Brush("BrushSecondaryText", Colors.Gray);

        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data            = geo,
            Stroke          = stroke,
            StrokeThickness = thick,
            StrokeLineCap   = PenLineCap.Round,
            StrokeJoin      = PenLineJoin.Round,
        });
        return new Viewbox { Width = size, Height = size, Child = canvas, VerticalAlignment = VerticalAlignment.Center };
    }

    // ── Выпадающий чеклист (как у MultiSelectBox, источник — записи целевой коллекции из БД) ──

    private Control BuildChecklist()
    {
        var fontBase = Res("FontSizeBase", 12.0);

        var valuesList = new ListBox
        {
            Background      = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            // Воздух вокруг строк — здесь, а не у карточки: Padding списка это Margin
            // презентера ВНУТРИ области прокрутки, поэтому полоса остается у края карточки.
            Padding         = new Thickness(8, 6),
            ItemsSource     = _shown,
            ItemTemplate    = BuildItemTemplate(fontBase),
        };
        valuesList.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0, 2)),
                        new Setter(ListBoxItem.MinHeightProperty, 0.0) },
        });
        void FlatRow(params string[] pseudo) =>
            valuesList.Styles.Add(new Style(x =>
            {
                Avalonia.Styling.Selector s = x.OfType<ListBoxItem>();
                foreach (var p in pseudo) s = s.Class(p);
                return s.Template().OfType<ContentPresenter>().Name("PART_ContentPresenter");
            })
            {
                Setters = { new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent) },
            });
        FlatRow();
        FlatRow(":pointerover");
        FlatRow(":selected");
        FlatRow(":selected", ":pointerover");

        _search = new TextBox
        {
            Watermark                = _cfg.EffectiveSearchField == RelationConfig.TitleField ? "Поиск по названию…"
                                     : _cfg.SearchKeys.Count > 1 ? "Поиск по свойствам…" : "Поиск по свойству…",
            Height                   = 30,
            FontSize                 = fontBase,
            Background               = Brush("BrushBg", Colors.White),
            CornerRadius             = Radius("RadiusBase", 6),
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin                   = new Thickness(0, 0, 0, 4),
        };
        _search.TextChanged += (_, _) => RefreshList(_search.Text);

        var valuesCard = new Border
        {
            Background      = Brush("BrushBg", Colors.White),
            BorderBrush     = Brush("BrushSeparator", Colors.LightGray),
            BorderThickness = new Thickness(1),
            CornerRadius    = Radius("RadiusBase", 6),
            // Padding НУЛЕВОЙ: полоса прокрутки прилегает ко всем краям карточки без белого
            // зазора, воздух вокруг строк задан Padding'ом самого списка.
            // ClipToBounds — квадратные углы полосы срезаются скруглением карточки.
            Padding         = new Thickness(0),
            ClipToBounds    = true,
            Height          = Res("PopupListHeight", 232),
            // Пустой список — надпись «Список пуст» / «Ничего не найдено» вместо голой карточки.
            Child           = Controls.Components.EmptyListHint.Wrap(
                                  valuesList, _search, Brush("BrushSecondaryText", Colors.Gray), fontBase),
        };

        _conditionHint = new TextBlock
        {
            FontSize     = Res("FontSizeSmall", 11.0),
            Foreground   = Brush("BrushSecondaryText", Colors.Gray),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(2, 0, 2, 4),
            IsVisible    = false,
        };

        // Автосвязь: по каким свойствам подбирается (галочки поэтому неактивны) или почему не подбирается.
        _autoHint = new TextBlock
        {
            FontSize     = Res("FontSizeSmall", 11.0),
            Foreground   = Brush("BrushSecondaryText", Colors.Gray),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(2, 0, 2, 4),
            IsVisible    = false,
        };

        // Подсказка «показаны первые N — уточните в поиске» (видна, когда кандидатов больше капа).
        _moreHint = new TextBlock
        {
            FontSize   = Res("FontSizeSmall", 11.0),
            Foreground = Brush("BrushSecondaryText", Colors.Gray),
            Margin     = new Thickness(2, 4, 2, 0),
            IsVisible  = false,
        };

        // Ширина фиксированная (общий стандарт всплывающих списков): длинные названия
        // записей дочитываются горизонтальной полосой карточки, а не растягивают попап.
        var stack = new StackPanel { Spacing = 2, Width = Res("PopupContentWidth", 240) };
        stack.Children.Add(_search);
        stack.Children.Add(_autoHint);
        stack.Children.Add(_conditionHint);
        stack.Children.Add(valuesCard);
        stack.Children.Add(_moreHint);

        return new Border
        {
            Background      = Brush("BrushSecondaryBg", Colors.WhiteSmoke),
            BorderBrush     = Brush("BrushSeparator", Colors.LightGray),
            BorderThickness = new Thickness(1),
            CornerRadius    = Radius("RadiusMedium", 8),
            Padding         = new Thickness(12),
            BoxShadow       = BoxShadows.Parse("0 4 16 0 #22000000"),
            Child           = stack,
        };
    }

    // Строка: галочка (выбор) + название + стрелка «→» (переход к записи).
    private IDataTemplate BuildItemTemplate(double fontBase) =>
        new FuncDataTemplate<RelationOptionVm>((_, _) =>
        {
            var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            name.Bind(TextBlock.TextProperty, new Binding(nameof(RelationOptionVm.Title)));
            var detail = new TextBlock
            {
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = Math.Max(9, fontBase - 1),
                Foreground = Brush("BrushSecondaryText", Colors.Gray),
            };
            detail.Bind(TextBlock.TextProperty, new Binding(nameof(RelationOptionVm.Detail)));
            detail.Bind(IsVisibleProperty, new Binding(nameof(RelationOptionVm.HasDetail)));
            // Пометка в самой строке, не в подсказке: выбранная раньше запись вне условия выбора.
            var outside = new TextBlock
            {
                Text = "не подходит под условие выбора",
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = Math.Max(9, fontBase - 1),
                Foreground = Brush("BrushOrange", Colors.DarkOrange),
            };
            outside.Bind(IsVisibleProperty, new Binding(nameof(RelationOptionVm.Outside)));
            var label = new StackPanel();
            label.Children.Add(name);
            label.Children.Add(detail);
            label.Children.Add(outside);

            var cb = new CheckBox
            {
                Content                    = label,
                FontSize                   = fontBase,
                HorizontalAlignment        = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin                     = new Thickness(0),
                Cursor                     = new Cursor(StandardCursorType.Hand),
            };
            cb.IsEnabled = _canSelectForList;
            cb.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(RelationOptionVm.Checked)) { Mode = BindingMode.TwoWay });

            // «Карандаш» — перейти к связанной записи (иконка из реестра, вместо стрелки).
            var editHit = new Border
            {
                Background = Brushes.Transparent,
                Padding    = new Thickness(6, 2),
                Child      = BuildIcon("IconPencil"),
                Cursor     = new Cursor(StandardCursorType.Hand),
            };
            editHit.PointerPressed += (s, e) =>
            {
                e.Handled = true;
                if ((s as Control)?.DataContext is RelationOptionVm vm)
                {
                    var targetRepo = vm.DbId == 0 ? _repo : RelationDbResolver.RepoFor(vm.DbId);
                    var collection = targetRepo?.GetRecord(vm.Id)?.CollectionId;
                    if (collection is null) return;
                    if (_popup is not null) _popup.IsOpen = false;
                    Navigate?.Invoke(this, (vm.DbId, vm.Id));   // база ЭТОГО пункта, не текущего конфига
                }
            };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(cb, 0);
            Grid.SetColumn(editHit, 1);
            grid.Children.Add(cb);
            grid.Children.Add(editHit);
            return grid;
        });

    // Показываем не более Cap кандидатов (на огромных коллекциях полный список невозможен — память/
    // зависание UI). Тянем Cap+1: если пришло больше — есть еще, показываем подсказку «уточните в поиске»
    // (точный остаток не считаем — COUNT по 10 млн на каждый ввод недопустим).
    private const int PickerCap = 200;

    private void CancelSearch()
    {
        _searchGeneration++;
        _pickerSearch?.Cancel();
        _pickerSearch?.Dispose();
        _pickerSearch = null;
    }

    private void RefreshList(string? query) => _ = RefreshListAsync(query);

    private async Task RefreshListAsync(string? query)
    {
        CancelSearch();
        var generation = _searchGeneration;
        var cancel = _pickerSearch = new CancellationTokenSource();
        var ct = cancel.Token;
        var q = query?.Trim() ?? "";
        var record = _recordId;
        var selected = _selected.ToArray();
        if (_moreHint is not null) { _moreHint.Text = "Поиск…"; _moreHint.IsVisible = true; }
        try
        {
            await Task.Delay(150, ct);
            var data = await Dispatcher.UIThread.InvokeAsync(() =>
                _repo.GetRelationPickerDataAsync(record, _cfg, q, selected, ct));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested || generation != _searchGeneration || record != _recordId || _popup is not { IsOpen: true }) return;
                ApplyPickerData(data, q);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось прочитать список связей");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _searchGeneration || ct.IsCancellationRequested) return;
                _shown.Clear();
                if (_moreHint is not null)
                {
                    _moreHint.Text = "Поиск недоступен";
                    _moreHint.IsVisible = true;
                }
            });
        }
    }

    private void ApplyPickerData(RelationPickerData data, string q)
    {
        _outside = data.SelectedOutside?.ToHashSet() ?? [];
        if (_conditionHint is not null)
        {
            _conditionHint.Text = data.ConditionProblem ?? (data.Condition.Length > 0 ? $"Только записи, где {data.Condition}" : "");
            _conditionHint.Foreground = data.ConditionProblem is null
                ? Brush("BrushSecondaryText", Colors.Gray) : Brush("BrushOrange", Colors.DarkOrange);
            _conditionHint.IsVisible = _conditionHint.Text.Length > 0;
        }
        foreach (var pair in data.SelectedLabels) _titles[pair.Key] = pair.Value;
        foreach (var value in data.Candidates) _titles[new(_cfg.TargetDbId, value.RecordId)] = value.Display;
        var list = new List<RelationOptionVm>();
        foreach (var target in _selected)
        {
            var label = _titles.GetValueOrDefault(target, "");
            if (q.Length > 0 && !data.SelectedSearch.GetValueOrDefault(target, "")
                .Contains(q, StringComparison.CurrentCultureIgnoreCase)) continue;
            list.Add(new RelationOptionVm(target, label, data.SelectedTitles.GetValueOrDefault(target, ""), this));
        }
        foreach (var result in data.Candidates)
        {
            var target = new RelationTarget(_cfg.TargetDbId, result.RecordId);
            if (!_selected.Contains(target)) list.Add(new RelationOptionVm(target, _titles.GetValueOrDefault(target, ""),
                _cfg.DisplayKeys.Contains(RelationConfig.TitleField) ? "" : result.Title, this));
        }
        _canSelectForList = CanSelect;
        _shown.Clear();
        _shown.AddRange(list);
        if (_moreHint is not null)
        {
            _moreHint.Text = $"Показаны первые {PickerCap} — уточните в поиске";
            _moreHint.IsVisible = data.HasMore;
        }
    }

    private void Toggle(RelationTarget target, bool on)
    {
        if (!CanSelect)
        {
            foreach (var item in _shown) item.Refresh();
            return;
        }
        var next = _cfg.Single && on ? new List<RelationTarget>() : new List<RelationTarget>(_selected);
        if (on) { if (!next.Contains(target)) next.Add(target); }
        else next.Remove(target);
        if (next.SequenceEqual(_selected)) return;
        // Снимается запись вне условия выбора (сама или заменой в режиме «Одну»): вернуть ее будет
        // нельзя — спрашиваем.
        var lost = _selected.Where(t => !next.Contains(t) && _outside.Contains(t)).ToList();
        if (lost.Count > 0) { _ = ConfirmOutsideRemovalAsync(next, lost, replacing: on); return; }
        Apply(next);
    }

    private async Task ConfirmOutsideRemovalAsync(List<RelationTarget> next, List<RelationTarget> lost, bool replacing)
    {
        var name = TitleText.FirstLine(_titles.GetValueOrDefault(lost[0], ""));
        var text = replacing
            ? $"Прежняя связь «{name}» не подходит под условие выбора: вернуть ее после замены будет нельзя."
            : $"Запись «{name}» не подходит под условие выбора: снятую связь выбрать снова будет нельзя.";
        var ok = TopLevel.GetTopLevel(this) is Window owner && await new SmartBase.Controls.Dialogs.ConfirmDialog(
            text, replacing ? "Заменить" : "Снять связь").ShowDialog<bool>(owner);
        if (ok) Apply(next);
        else foreach (var item in _shown) item.Refresh();
    }

    private void Apply(List<RelationTarget> next)
    {
        try
        {
            if (_recordId > 0) _repo.SetLinkEdges(_fieldId, _recordId, next);
        }
        catch (RelationConditionException ex)
        {
            // Список устарел: запись перестала подходить, пока он был открыт.
            AppNotifications.Info(ex.Message + " Список обновлен.");
            RefreshList(_search?.Text);
            return;
        }
        _cache?.Invalidate(_fieldId, _recordId);
        ReloadSelected(preferCache: false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static IBrush Brush(string key, Color fallback) =>
        Application.Current!.TryFindResource(key, out var r) && r is IBrush b ? b : new SolidColorBrush(fallback);

    private static CornerRadius Radius(string key, double fallback) =>
        Application.Current!.TryFindResource(key, out var r) && r is CornerRadius c ? c : new CornerRadius(fallback);

    private static double Res(string key, double fallback) =>
        Application.Current!.TryFindResource(key, out var r) && r is double d ? d : fallback;

    private sealed class RelationOptionVm(RelationTarget target, string title, string recordTitle, RelationSelectBox owner) : INotifyPropertyChanged
    {
        public int    Id    => target.RecordId;
        public int    DbId  => target.DbId;   // база цели этого пункта (0 — та же БД, >0 — межбазовая)
        // Строка списка — одна строка: показываем первую (поиск выше идет по полному названию).
        public string Title => title.Length == 0 ? "(пусто)" : TitleText.FirstLine(title);
        public string Detail => recordTitle is RelationProperty.Unavailable or RelationProperty.UnknownBase ? "" : TitleText.FirstLine(recordTitle);
        public bool HasDetail => Detail.Length > 0 && Detail != Title;
        public bool Outside => owner._outside.Contains(target);

        public bool Checked
        {
            get => owner._selected.Contains(target);
            set
            {
                owner.Toggle(target, value);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked)));
            }
        }

        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked)));

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
