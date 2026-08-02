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
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Utils;
using SmartBase.ViewModels.ConstructorDb;

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

    private readonly List<int>                     _selected = [];
    // id выбранной цели → TargetDbId ЕГО ребра: переход по «карандашу» идёт в СВОЮ базу ребра, а не в
    // цель текущего конфига поля (иначе после пере-настройки/смешанных рёбер открылась бы пустая/чужая
    // запись). Для кандидатов из пикера используется _cfg.TargetDbId (они из текущей целевой базы).
    private readonly Dictionary<int, int>          _selectedDb = new();
    private readonly Dictionary<int, string>       _titles   = new();
    private readonly AvaloniaList<RelationOptionVm> _shown    = [];
    private readonly TextBlock _valueText;
    private readonly TextBlock _placeholder;
    private readonly Popup?    _popup;
    private TextBox?   _search;
    private TextBlock? _moreHint;
    private int _recordId;

    /// <summary>Поднимается после изменения набора связей.</summary>
    public event EventHandler? Changed;
    /// <summary>Переход к связанной записи — (DbId цели, Id записи). DbId=0 — та же БД, &gt;0 —
    /// межбазовая цель (открыть из её БД). Пункт «карандаш» у строки списка.</summary>
    public event EventHandler<(int DbId, int RecordId)>? Navigate;

    // Репозиторий целевой БД: та же (внутрибазовая связь) или чужая (межбазовая — через резолвер
    // реестра). null для межбазовой — целевая БД недоступна (показ/пикер деградируют мягко).
    private ConstructorRepository? TargetRepo() => _cfg.IsCrossDb ? RelationDbResolver.RepoFor(_cfg.TargetDbId) : _repo;

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
        }

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
    /// Тот же RecordId, но МЕЖБАЗОВАЯ связь → тоже перечитать СВЕЖИМ: её нет в батч-кеше, поэтому
    /// правка/фон иначе не видны (внутрибазовую держит свежей батч-кеш, лишний раз не дёргаем).</summary>
    public void OnRowFilled(int recordId)
    {
        if (recordId != _recordId) { RecordId = recordId; return; }
        if (_recordId <= 0) return;
        // Тот же RecordId, но строку пере-Fill'или. Read-ячейка (editing:false) перечитывает показ,
        // чтобы ВНЕШНЕЕ изменение отразилось сразу — в т.ч. переименование ЦЕЛИ: VirtualRowList
        // пере-праймил батч-кеш свежими названиями (RefreshRelationTargets), и здесь мы их подхватываем.
        // Межбазовую читаем свежей (её нет в кеше), внутрибазовую — из обновлённого кеша (дёшево).
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
        _selectedDb.Clear();
        if (_recordId > 0)
        {
            // Быстрый путь таблицы: батч-кеш держит ТОЛЬКО внутрибазовые рёбра (TargetDbId=0) с уже
            // резолвнутыми названиями. Годится лишь для внутрибазового поля при прогретом кеше; иначе
            // (и всегда для межбазового поля) — резолвим по рёбрам, КАЖДОЕ по своему TargetDbId.
            if (!_cfg.IsCrossDb && preferCache && _cache is not null
                && _cache.TryGet(_fieldId, _recordId, out var cached))
            {
                foreach (var (id, title) in cached) { _selected.Add(id); _selectedDb[id] = 0; _titles[id] = title; }
            }
            else LoadSelected();
        }
        RefreshDisplay();
        if (_popup is { IsOpen: true }) RefreshList(_search?.Text);
    }

    // Резолв КАЖДОГО ребра по ЕГО СОБСТВЕННОМУ TargetDbId (а не по цели текущего конфига поля): ребро,
    // созданное на другую БД, всегда резолвится ИЗ ТОЙ БД — даже если позже цель поля пере-настроили.
    // Так показ остаётся честным: нет ложных совпадений Id с чужой локальной записью.
    //
    // Единая логика обрыва (как внутри базы, так и межбазы):
    //  • БД цели ДОСТУПНА, но записи в ней нет (удалена/перенесена) → чип пропадает (не в _selected):
    //    пусто → «связи нет». На правке ячейки такое ребро вычищается само (SetLinks по _selected).
    //  • БД цели НЕДОСТУПНА (сервер лёг / путь-файл пропал / грузится / отключена) → показываем
    //    «(база недоступна)» и ДЕРЖИМ ребро (база вернётся — связь восстановится).
    private void LoadSelected()
    {
        var edges = _repo.GetLinkEdges(_fieldId, _recordId);   // (ToRecordId, TargetDbId) в порядке Position
        if (edges.Count == 0) return;

        // Обрыв решаем по ПАРЕ (ToRecordId, TargetDbId), а не по голому id: осиротевшее межбазовое
        // ребро (TargetDbId>0), чей id СЛУЧАЙНО совпал с живой внутрибазовой записью, не должно
        // прятать валидную внутрибазовую связь (и убивать её на следующей правке через SetLinks).
        var gone = new HashSet<(int To, int Db)>();   // база доступна, но цели нет → обрыв → «связи нет»
        foreach (var grp in edges.GroupBy(e => e.TargetDbId))
        {
            var repo = grp.Key == 0 ? _repo : RelationDbResolver.RepoFor(grp.Key);
            var ids  = grp.Select(e => e.ToRecordId).Distinct().ToList();
            var map  = repo?.GetRecordTitles(ids);   // null → БД цели недоступна (временно)
            foreach (var id in ids)
            {
                if (map is null)                          _titles[id] = "(база недоступна)";
                else if (map.TryGetValue(id, out var t))  _titles[id] = t;
                else                                      gone.Add((id, grp.Key));
            }
        }
        foreach (var (toId, dbId) in edges)
            if (!gone.Contains((toId, dbId))) { _selected.Add(toId); _selectedDb[toId] = dbId; }
    }

    private void OpenPopup()
    {
        if (_recordId <= 0 || _popup is null) return;
        RefreshList("");
        _popup.IsOpen = true;
        _search?.Focus();
    }

    // Показ закрытого вида — названия через разделитель поля (как read-ячейка мультисписка).
    private void RefreshDisplay()
    {
        _valueText.Text        = string.Join(_separator, _selected.Select(id => _titles.GetValueOrDefault(id, "")));
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
            Padding         = new Thickness(0),
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
            Watermark                = "Поиск по названию…",
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
            Padding         = new Thickness(8, 6),
            Height          = 232,
            Child           = valuesList,
        };

        // Подсказка «показаны первые N — уточните в поиске» (видна, когда кандидатов больше капа).
        _moreHint = new TextBlock
        {
            FontSize   = Res("FontSizeSmall", 11.0),
            Foreground = Brush("BrushSecondaryText", Colors.Gray),
            Margin     = new Thickness(2, 4, 2, 0),
            IsVisible  = false,
        };

        var stack = new StackPanel { Spacing = 2, MinWidth = 260 };
        stack.Children.Add(_search);
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

            var cb = new CheckBox
            {
                Content                    = name,
                FontSize                   = fontBase,
                HorizontalAlignment        = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin                     = new Thickness(0),
                Cursor                     = new Cursor(StandardCursorType.Hand),
            };
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
    // зависание UI). Тянем Cap+1: если пришло больше — есть ещё, показываем подсказку «уточните в поиске»
    // (точный остаток не считаем — COUNT по 10 млн на каждый ввод недопустим).
    private const int PickerCap = 200;

    private void RefreshList(string? query)
    {
        var q       = query?.Trim() ?? "";
        // Кандидаты — из ЦЕЛЕВОЙ БД (для межбазовой связи чужой). Само-исключение только внутри
        // своей БД (в другой БД тот же Id — иная запись).
        var results = TargetRepo()?.SearchRecordTitles(_cfg.TargetCollectionId, q, PickerCap + 1, _cfg.IsCrossDb ? 0 : _recordId)
                      ?? [];
        var hasMore = results.Count > PickerCap;
        if (hasMore) results = results.Take(PickerCap).ToList();
        // Кандидаты всегда из ТЕКУЩЕЙ целевой базы (_cfg.TargetDbId). Не затираем название выбранного
        // ребра, чья база ОТЛИЧАЕТСЯ (межбазовое/смешанное — см. C4): при коллизии id кандидатское имя
        // из целевой базы подменило бы имя из чужой базы (навигация-DbId осталась бы верной, а текст — нет).
        foreach (var (id, title) in results)
            if (!(_selectedDb.TryGetValue(id, out var edgeDb) && edgeDb != _cfg.TargetDbId))
                _titles[id] = title;

        var list = new List<RelationOptionVm>();
        // Уже выбранные — ВСЕГДА с базой СВОЕГО ребра (переход туда, откуда связь реально идёт), и в
        // пустом списке, И В ПОИСКЕ: иначе при коллизии id «→» уводил бы в базу текущего конфига поля
        // (исходный симптом C4). Кандидаты приходят только из целевой базы поля, поэтому выбранная
        // цель в ЧУЖОЙ базе в results не попадает — отбираем её по уже известному названию, чтобы при
        // поиске она не исчезала из списка.
        foreach (var id in _selected)
        {
            var title = _titles.GetValueOrDefault(id, "");
            if (q.Length > 0 && title.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
            list.Add(new RelationOptionVm(id, title, _selectedDb.GetValueOrDefault(id, _cfg.TargetDbId), this));
        }
        // Кандидаты — из ТЕКУЩЕЙ целевой базы поля.
        foreach (var (id, title) in results)
            if (!_selected.Contains(id)) list.Add(new RelationOptionVm(id, title, _cfg.TargetDbId, this));
        _shown.Clear();
        _shown.AddRange(list);

        if (_moreHint is not null)
        {
            _moreHint.Text      = $"Показаны первые {PickerCap} — уточните в поиске";
            _moreHint.IsVisible = hasMore;
        }
    }

    private void Toggle(int id, bool on)
    {
        if (_cfg.Single)
        {
            _selected.Clear();
            if (on) _selected.Add(id);
            foreach (var it in _shown) it.Refresh();
        }
        else if (on)
        {
            if (!_selected.Contains(id)) _selected.Add(id);
        }
        else
        {
            _selected.Remove(id);
        }

        if (_recordId > 0)
        {
            // Оборванные ссылки (цель удалена при живой базе) в _selected не попадают (LoadSelected),
            // поэтому SetLinks по _selected сам вычищает их рёбра — «чистка при правке».
            _repo.SetLinks(_fieldId, _recordId, _selected);
            _cache?.Invalidate(_fieldId, _recordId);   // кеш устарел — следующее чтение возьмёт свежее
        }
        RefreshDisplay();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static IBrush Brush(string key, Color fallback) =>
        Application.Current!.TryFindResource(key, out var r) && r is IBrush b ? b : new SolidColorBrush(fallback);

    private static CornerRadius Radius(string key, double fallback) =>
        Application.Current!.TryFindResource(key, out var r) && r is CornerRadius c ? c : new CornerRadius(fallback);

    private static double Res(string key, double fallback) =>
        Application.Current!.TryFindResource(key, out var r) && r is double d ? d : fallback;

    private sealed class RelationOptionVm(int id, string title, int dbId, RelationSelectBox owner) : INotifyPropertyChanged
    {
        public int    Id    => id;
        public int    DbId  => dbId;   // база цели этого пункта (0 — та же БД, >0 — межбазовая)
        public string Title => title;

        public bool Checked
        {
            get => owner._selected.Contains(id);
            set
            {
                owner.Toggle(id, value);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked)));
            }
        }

        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked)));

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
