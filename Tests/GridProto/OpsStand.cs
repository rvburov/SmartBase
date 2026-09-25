// Стенд этапа 5 плана drawn-grid.md («Операции») на настоящем TableView с рисованной сеткой:
//   фильтр и сортировка из шапки через настоящее окно фильтра (кнопка заголовка, список значений,
//   каскад, поиск с Enter, «Готово», снятие), отбор и сортировка в фоне, память отбора коллекции;
//   операции с колонками (скрыть и показать, сдвиг меню и клавишами, переименование «Названия»,
//   создание в конце и после, дубль, смена типа, удаление колонки с отбором) — ширины по ключам,
//   строки и прокрутка на месте; операции с записями (создание при отборе и без, сдвиг меню и
//   клавишами, удаление, дубль, «Расположение», «Действия», «Отображение записей»), ширина «№» по
//   точному счету из фона, стандартная ширина столбцов из настроек.
// Запускается отдельным процессом: переключатель разработчика читается раз на процесс.

using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using SmartBase.Controls.Components;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Utils;
using SmartBase.ViewModels.ConstructorDb;
using SmartBase.Views.ConstructorDb;
using SmartBase.Views.ConstructorDb.DrawnGrid;

static class OpsStand
{
    // Настройки процесса — в памяти: память фильтров и стандартная ширина столбцов пишутся сюда.
    private sealed class MemoryStore : ISettingsDatabase
    {
        private readonly Dictionary<string, string> _d = new();
        public string? Get(string key) => _d.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => _d[key] = value;
        public void Remove(string key) => _d.Remove(key);
        public IReadOnlyList<string> KeysWithPrefix(string prefix) =>
            [.. _d.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))];
    }

    public static int Run(string path, string outDir, string collectionName)
    {
        Environment.SetEnvironmentVariable("SMARTBASE_DRAWN_GRID", "1");
        if (!DrawnGridSwitch.Enabled) { Console.WriteLine("Переключатель прочитан раньше без переменной — --ops запускается отдельным процессом."); return 4; }
        AppSettings.AttachStore(new MemoryStore());
        Dispatcher.UIThread.UnhandledException += (_, e) => Console.WriteLine($"    [исключение UI] {e.Exception}");

        var tmp = Path.Combine(outDir, "ops-" + Path.GetFileName(path));
        File.Copy(path, tmp, overwrite: true);
        using var mgr  = DatabaseManager.Open(9405, "gridops", tmp);
        var repo = mgr.Constructor;
        var cid  = repo.GetAllCollections().First(c => c.Name == collectionName).Id!.Value;

        var failed = 0;
        void Expect(bool ok, string what)
        {
            Console.WriteLine($"  {(ok ? "ок    " : "ПРОВАЛ")} {what}");
            if (!ok) failed++;
        }

        // Видимы «Название» и пять полей: текст, число, список, статус, дата.
        var snap0 = repo.ReadOpenSnapshot(cid);
        DbField F(string type) => snap0.Fields.First(f => f.FieldType == type);
        var fText = F("text"); var fNum = F("number"); var fSel = F("select"); var fStat = F("status"); var fDate = F("date");
        var shownFields = new[] { fText, fNum, fSel, fStat, fDate };
        var shownIds    = shownFields.Select(f => f.Id!.Value).ToHashSet();   // поля из базы — новые экземпляры, сравнение по id
        repo.SaveColumnDisplay(cid, snap0.Fields.Select(f => (f.Id!.Value.ToString(), shownIds.Contains(f.Id!.Value), 1)));
        string K(DbField f) => f.Id!.Value.ToString();
        var title = TableRowVm.TitleKey;
        var num   = TableColumnDef.NumKey;

        var w     = new Window { Width = 1600, Height = 900 };
        var table = new TableView();
        w.Content = table;
        w.Show();
        Settle(w);

        IReadOnlyList<TableColumnDef> defs = [];
        var deleted = new List<IReadOnlyList<int>>(); var duplicated = new List<IReadOnlyList<int>>();
        var relocated = new List<IReadOnlyList<int>>(); var acted = new List<IReadOnlyList<int>>();
        table.ColumnsBuilt              += (_, d) => defs = d;
        table.RecordsDeleteRequested    += (_, ids) => deleted.Add(ids);
        table.RecordDuplicateRequested  += (_, ids) => duplicated.Add(ids);
        table.RecordRelocateRequested   += (_, ids) => relocated.Add(ids);
        table.DataActions = () => [new DataAction("Выгрузить", ids => { acted.Add(ids); return Task.CompletedTask; })];

        // Открытие — как у раздела с переключателем: снимок без COUNT, точный счет в фоне.
        table.Load(repo, cid, repo.ReadOpenSnapshot(cid, deferCount: true));
        Pump();
        var p = table.DrawnPanel!;
        var s = p.Surface;
        WaitUntil(s.VisibleCellsLoaded, TimeSpan.FromSeconds(20));
        var sOrigin = s.TranslatePoint(new Point(0, 0), w)!.Value;
        var total   = repo.CountRecords(cid);

        ColumnLayout L() => s.Columns;
        double Width(string key) => L().IndexOf(key) is var c and >= 0 ? L().Width(c) : -1;
        int Col(string key) => defs.ToList().FindIndex(d => d.Key == key);
        Point ToWindow(Point surfacePoint) => new(sOrigin.X + surfacePoint.X, sOrigin.Y + surfacePoint.Y);
        Point Center(int row, string key)
        {
            Reveal(key);
            var c = L().IndexOf(key);
            return ToWindow(new Point(L().Left(c) - s.OffsetX + L().Width(c) / 2,
                                      s.HeaderHeight + s.RowLayout.Top(row) - s.OffsetY + s.RowPitch / 2));
        }
        // Колонка — в окно (минимальным сдвигом вбок), как сделал бы человек перед щелчком по ней.
        void Reveal(string key) { p.ScrollToCell(s.VisibleRows().First, key); Pump(); }
        Point HeaderAt(string key)
        {
            Reveal(key);
            return ToWindow(new Point(L().Left(L().IndexOf(key)) - s.OffsetX + 20, s.HeaderHeight / 2));
        }
        void Click(Point pt, RawInputModifiers m = RawInputModifiers.None, MouseButton button = MouseButton.Left)
        {
            w.MouseMove(pt, m); w.MouseDown(pt, button, m); w.MouseUp(pt, button, m); Pump();
        }
        void Gesture(string action)
        {
            var g = MenuGestures.For(action, table)!;
            w.KeyPress(g.Key, (RawInputModifiers)(int)g.KeyModifiers); w.KeyRelease(g.Key, (RawInputModifiers)(int)g.KeyModifiers); Pump();
        }
        // Список подключен, видимые ячейки дочитаны, точный счет из фона пришел.
        void Settled()
        {
            WaitUntil(() => p.QueryTask.IsCompleted && s.VisibleCellsLoaded() && p.Rows is { CountIsEstimate: false }, TimeSpan.FromSeconds(30));
            Pump();
        }
        int DataRows() => p.RowCount - 1;
        TableRowVm Row(int i) => p.Rows!.TryGetCached(i)!;
        // Первые строки среза — с данными (для проверок порядка и отбора).
        List<TableRowVm> Head(int n)
        {
            s.OffsetY = 0; Pump();
            WaitUntil(s.VisibleCellsLoaded, TimeSpan.FromSeconds(20));
            return Enumerable.Range(0, Math.Min(n, DataRows())).Select(i => p.Rows!.TryGetCached(i)).OfType<TableRowVm>().ToList();
        }

        // ── Окно фильтра: элементы и действия ──────────────────────────────────
        Control Content(Flyout fl) => (Control)fl.Content!;
        IEnumerable<T> Find<T>(Flyout fl) where T : class => Content(fl).GetSelfAndLogicalDescendants().OfType<T>();
        ListBox? ValuesList(Flyout fl) => Find<ListBox>(fl).FirstOrDefault(l => l.ItemsSource is System.Collections.IList { Count: > 0 });
        List<object> Values(Flyout fl) => ValuesList(fl)?.ItemsSource is System.Collections.IList list ? list.Cast<object>().ToList() : [];
        string Raw(object vm)   => (string)vm.GetType().GetProperty("Raw")!.GetValue(vm)!;
        int    Count(object vm) => int.Parse((string)vm.GetType().GetProperty("CountText")!.GetValue(vm)!);
        bool   IsOn(object vm)  => (bool)vm.GetType().GetProperty("Checked")!.GetValue(vm)!;
        void   SetOn(object vm, bool on) { vm.GetType().GetProperty("Checked")!.SetValue(vm, on); Pump(); }
        CheckBox SelectAll(Flyout fl) => Find<CheckBox>(fl).First(c => c.Content is string t && t.StartsWith("(Выделить все"));
        TextBox  Search(Flyout fl)    => Find<TextBox>(fl).First(t => t.Watermark as string == "Поиск значений...");
        // Кнопки окна, а не галочки значений: значение списка тоже может называться «Готово».
        Button   ButtonWith(Flyout fl, string text) =>
            Find<Button>(fl).Where(b => b is not ToggleButton)
                            .First(b => b.Content as string == text
                                        || b.Content is Control c && c.GetSelfAndLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == text));
        void Press(Button b) { b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, b)); Pump(); }
        void Enter(TextBox box)
        {
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter, Source = box });
            Pump();
        }
        Flyout? OpenFilter(string key)
        {
            Reveal(key);
            var btn = s.FilterButtonRect(key);
            if (btn is null) return null;
            var before = table.LastOpenedFilter;
            Click(ToWindow(btn.Value.Center));
            var fl = table.LastOpenedFilter;
            if (fl is null || ReferenceEquals(fl, before)) return null;
            WaitUntil(() => Values(fl).Count > 0, TimeSpan.FromSeconds(20));
            return fl;
        }
        // Оставить отмеченным только набор значений и нажать «Готово».
        void Keep(string key, params string[] raws)
        {
            var fl = OpenFilter(key)!;
            SelectAll(fl).IsChecked = false; Pump();
            foreach (var vm in Values(fl)) if (raws.Contains(Raw(vm))) SetOn(vm, true);
            Press(ButtonWith(fl, "Готово"));
            Settled();
        }
        void ClearFilter(string key)
        {
            var fl = OpenFilter(key)!;
            SelectAll(fl).IsChecked = true; Pump();
            Press(ButtonWith(fl, "Снять сортировку"));
            Press(ButtonWith(fl, "Готово"));
            Settled();
        }
        void Sort(string key, string label)
        {
            var fl = OpenFilter(key)!;
            Press(ButtonWith(fl, label));
            Press(ButtonWith(fl, "Готово"));
            Settled();
        }
        MenuItem? Item(ContextMenu? menu, string text) =>
            menu?.Items.OfType<MenuItem>().FirstOrDefault(i =>
                i.Header as string == text
                || i.Header is Control c && c.GetSelfAndLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == text));
        void Choose(MenuItem? item) { item?.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item)); Pump(); }

        Console.WriteLine($"Коллекция «{collectionName}»: {total:N0} записей, колонки: {string.Join(", ", Enumerable.Range(0, L().Count).Select(i => L()[i].Header))}");

        // ── Открытие без COUNT и ширина «№» ────────────────────────────────────
        Console.WriteLine("Открытие:");
        Expect(WaitUntil(() => p.Rows is { CountIsEstimate: false } r && r.DataCount == total, TimeSpan.FromSeconds(20)),
               $"точный счет пришел из фона: {p.Rows?.DataCount:N0}");
        var family   = Avalonia.Controls.Documents.TextElement.GetFontFamily(p);
        var fontSize = p.TryFindResource("FontSizeBase", out var fsz) && fsz is double d ? d : 12;
        var numText  = new Avalonia.Media.FormattedText(total.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                                       System.Globalization.CultureInfo.InvariantCulture, Avalonia.Media.FlowDirection.LeftToRight,
                                                       new Avalonia.Media.Typeface(family), fontSize, null).Width;
        var numWant  = Math.Max(36, Math.Ceiling(numText) + 16);
        Expect(Math.Abs(Width(num) - numWant) < 0.51, $"«№» под число записей: {Width(num)} (ждем {numWant})");

        // ── Фильтр и сортировка из шапки ───────────────────────────────────────
        Console.WriteLine("Фильтр из шапки:");
        var btnSel = s.FilterButtonRect(K(fSel))!.Value;
        var flSel  = OpenFilter(K(fSel));
        Expect(flSel is { IsOpen: true }, "клик по кнопке фильтра — окно фильтра открыто");
        Expect(flSel?.Target is Control anchor && anchor.TranslatePoint(new Point(0, 0), s) is { } at
               && Math.Abs(at.X - btnSel.X) < 0.5 && Math.Abs(at.Y - btnSel.Y) < 0.5
               && Math.Abs(anchor.Bounds.Width - btnSel.Width) < 0.5 && Math.Abs(anchor.Bounds.Height - btnSel.Height) < 0.5,
               "окно прикреплено к кнопке заголовка");
        Expect(table.CurrentCellRange is null && table.SelectedColumnKeys.Count == 0, "нажатие на кнопку фильтра колонку не выделяет");
        var dbCounts = repo.GetColumnCountsAsync(cid, K(fSel)).Result;
        var listed   = Values(flSel!).Select(v => (Raw(v), Count(v))).ToList();
        Expect(listed.Count == dbCounts.Count && dbCounts.All(c => listed.Contains((c.Value, c.Count))),
               $"значения и счетчики — из базы ({listed.Count} значений)");
        Expect(Values(flSel!).All(IsOn) && SelectAll(flSel!).IsChecked == true, "без фильтра отмечено все");
        flSel!.Hide(); Pump();

        var valueV = dbCounts.Where(c => c.Value != "").OrderByDescending(c => c.Count).First();
        var filterSel = new Dictionary<string, HashSet<string>> { [K(fSel)] = [valueV.Value] };
        Keep(K(fSel), valueV.Value);
        Expect(s.IsColumnFiltered(K(fSel)) && !s.IsColumnFiltered(K(fStat)), "воронка на отобранной колонке, на соседней — шеврон");
        Expect(p.Rows!.Source is IdListRowSource && DataRows() == valueV.Count, $"в таблице только «{valueV.Value}»: {DataRows()} строк");
        Expect(Head(15).All(r => r[K(fSel)] == valueV.Value), "видимые строки — с этим значением");
        Expect(table.LastOpenedFilter is { IsOpen: false }, "«Готово» закрыло окно");

        var flStat = OpenFilter(K(fStat))!;
        var statCounts = Values(flStat).Select(v => (Raw: Raw(v), Count: Count(v))).ToList();
        Expect(statCounts.Sum(c => c.Count) == valueV.Count, $"каскад: счетчики статуса — по отобранным строкам (сумма {statCounts.Sum(c => c.Count)})");
        var dbStat = repo.GetColumnCountsAsync(cid, K(fStat), filterSel).Result;
        Expect(dbStat.Count == statCounts.Count, "каскад: значения статуса — те же, что считает база");

        // Сортировка и поиск с Enter — разом.
        var statS = statCounts.OrderByDescending(c => c.Count).First(c => c.Raw != "");
        Press(ButtonWith(flStat, "От Я до А"));
        var search = Search(flStat);
        search.Text = statS.Raw; Pump();
        Expect(Values(flStat).Where(v => Raw(v).Contains(statS.Raw, StringComparison.OrdinalIgnoreCase)).All(IsOn), "поиск отмечает найденное");
        Enter(search);
        Settled();
        var bothFilters = new Dictionary<string, HashSet<string>>
        {
            [K(fSel)]  = [valueV.Value],
            [K(fStat)] = [.. statCounts.Select(c => c.Raw).Where(r => r.Contains(statS.Raw, StringComparison.OrdinalIgnoreCase))],
        };
        var bothIds = repo.GetFilteredRecordIds(cid, bothFilters);
        Expect(!flStat.IsOpen && s.IsColumnFiltered(K(fStat)) && DataRows() == bothIds.Count,
               $"Enter в поиске: отбор и сортировка применились разом ({DataRows()} строк)");

        var flSel2 = OpenFilter(K(fSel))!;
        var first  = Values(flSel2).First();
        Expect(Raw(first) == valueV.Value && IsOn(first) && SelectAll(flSel2).IsChecked == false,
               "повторное открытие: отобранное первым и отмечено, «(Выделить все)» снят");
        SelectAll(flSel2).IsChecked = true; Pump();
        Press(ButtonWith(flSel2, "Готово"));
        Settled();
        Expect(!s.IsColumnFiltered(K(fSel)) && s.IsColumnFiltered(K(fStat)), "отметили все — воронка на списке погасла, на статусе осталась");
        ClearFilter(K(fStat));
        Expect(p.Rows!.Source is PlainRowSource && DataRows() == total && !s.IsColumnFiltered(K(fStat)), "фильтры сняты — все записи в порядке таблицы");

        Sort(K(fStat), "От А до Я");
        Expect(p.Rows!.Source is SortedRowSource { Asc: true }, "сортировка статуса — по индексу, без списка id");
        ClearFilter(K(fStat));
        Sort(K(fNum), "По убыванию");
        var cmpNum = ColumnSort.BuildComparer("number", asc: false);
        var headNum = Head(30).Select(r => r[K(fNum)]).ToList();
        Expect(p.Rows!.Source is IdListRowSource && headNum.Zip(headNum.Skip(1)).All(t => cmpNum(t.First, t.Second) <= 0),
               $"сортировка числа по убыванию — списком из фона: {string.Join(" ", headNum.Take(5))} …");

        // Смена отбора: горизонтальная прокрутка остается, вертикальная — к началу.
        s.OffsetX = 120; s.OffsetY = s.RowPitch * 40; Pump();
        var x0 = s.OffsetX;
        Keep(K(fSel), valueV.Value);
        Expect(Math.Abs(s.OffsetX - x0) < 0.5 && s.OffsetY == 0, $"новый отбор: прокрутка вбок на месте ({s.OffsetX}), по строкам — к началу");
        ClearFilter(K(fSel));
        ClearFilter(K(fNum));
        Expect(p.Rows!.Source is PlainRowSource, "сортировка снята");

        // ── Память отбора ──────────────────────────────────────────────────────
        Console.WriteLine("Запомненный отбор:");
        ConstructorSettings.SetRememberFilters(true);
        Keep(K(fSel), valueV.Value);
        var before = p.Rows;
        table.Load(repo, cid, repo.ReadOpenSnapshot(cid, deferCount: true));
        Expect(ReferenceEquals(p.Rows, before), "повторное открытие той же коллекции: пока отбор считается, на экране прежние строки");
        Settled();
        Expect(s.IsColumnFiltered(K(fSel)) && DataRows() == valueV.Count, $"отбор восстановлен ({DataRows()} строк)");
        table.Clear();
        table.Load(repo, cid, repo.ReadOpenSnapshot(cid, deferCount: true));
        Expect(p.Rows is { DataCount: 0 }, "открытие заново: пока отбор считается — пустая таблица, а не все записи");
        Settled();
        Expect(s.IsColumnFiltered(K(fSel)) && DataRows() == valueV.Count, "после расчета — отобранные строки");
        ClearFilter(K(fSel));
        ConstructorSettings.SetRememberFilters(false);
        table.Load(repo, cid, repo.ReadOpenSnapshot(cid, deferCount: true));
        Settled();
        Expect(p.Rows!.Source is PlainRowSource && DataRows() == total, "без памяти — открытие без отбора");

        // ── Операции с колонками ───────────────────────────────────────────────
        Console.WriteLine("Колонки (ручная ширина):");
        var model = new Dictionary<string, double>
        {
            [title] = 400, [K(fText)] = 500, [K(fNum)] = 350, [K(fSel)] = 450, [K(fStat)] = 400, [K(fDate)] = 450,
        };
        repo.SaveColumnWidths(cid, model);
        table.SetColResizeMode(true);
        table.Load(repo, cid, repo.ReadOpenSnapshot(cid, deferCount: true));
        Settled();
        bool WidthsKept(params string[] except) =>
            model.Where(kv => !except.Contains(kv.Key) && L().IndexOf(kv.Key) >= 0).All(kv => Math.Abs(Width(kv.Key) - kv.Value) < 0.51);
        Expect(WidthsKept(), "ширины из модели: " + string.Join(", ", model.Keys.Select(k => $"{Width(k)}")));
        var rows0 = p.Rows;
        s.OffsetX = 150; Pump();

        repo.SaveColumnDisplay(cid, repo.GetFields(cid).Select(f => (K(f), f.Id != fText.Id && shownIds.Contains(f.Id!.Value), 1)));
        table.ReloadColumns(); Pump();
        Expect(L().IndexOf(K(fText)) < 0 && Col(K(fText)) > 0 && WidthsKept(K(fText)), "скрыть колонку: ее нет в раскладке, ширины остальных те же");
        Expect(ReferenceEquals(p.Rows, rows0) && Math.Abs(s.OffsetX - 150) < 0.5, "строки и прокрутка вбок на месте");
        repo.SaveColumnDisplay(cid, repo.GetFields(cid).Select(f => (K(f), shownIds.Contains(f.Id!.Value), 1)));
        table.ReloadColumns(); Pump();
        Expect(Math.Abs(Width(K(fText)) - model[K(fText)]) < 0.51, "показать обратно — ширина прежняя");

        var orderBefore = table.MovableKeysInOrder();
        Click(HeaderAt(K(fSel)), button: MouseButton.Right);
        Choose(Item(table.LastOpenedMenu, "Сдвинуть вправо"));
        table.LastOpenedMenu?.Close(); Pump();
        var orderAfter = table.MovableKeysInOrder();
        Expect(orderAfter.IndexOf(K(fSel)) == orderBefore.IndexOf(K(fSel)) + 1 && L().IndexOf(K(fSel)) == L().IndexOf(K(fStat)) + 1,
               "меню шапки «Сдвинуть вправо» — колонка правее соседа");
        Expect(WidthsKept() && ReferenceEquals(p.Rows, rows0), "сдвиг: ширины по ключам, строки не пересобраны");
        Click(Center(3, K(fSel)));
        Gesture("table.moveColumnLeft");
        Expect(table.MovableKeysInOrder().SequenceEqual(orderBefore) && table.CurrentCellRange is { } cr && cr.AnchorColumn == Col(K(fSel)),
               "сдвиг клавишами от ячейки — колонка вернулась, рамка на ней же");
        Expect(WidthsKept(), "ширины после второго сдвига — те же");

        repo.SetSystemFieldName(cid, title, "Имя записи", "Название");
        table.RebuildColumn(title); Pump();
        Expect(defs.First(dd => dd.Key == title).Header == "Имя записи" && Math.Abs(Width(title) - model[title]) < 0.51,
               "переименование «Названия»: подпись новая, ширина та же");

        var fNew = repo.AddField(new DbField { CollectionId = cid, Name = "Новое", FieldType = "text", Position = 9000 });
        table.AddColumnForField(fNew.Id!.Value); Pump();
        Expect(L().IndexOf(K(fNew)) == L().Count - 2 && Math.Abs(Width(K(fNew)) - ConstructorSettings.ColWidth) < 0.51 && WidthsKept(),
               $"новое свойство — в конце, стандартной ширины ({Width(K(fNew))}), соседи свои ширины сохранили");
        var fAfter = repo.AddField(new DbField { CollectionId = cid, Name = "После числа", FieldType = "text", Position = 9001 });
        repo.MoveColumnAfter(cid, K(fAfter), K(fNum), [.. table.MovableKeysInOrder(), K(fAfter)]);
        table.ReloadColumns(); Pump();
        Expect(L().IndexOf(K(fAfter)) == L().IndexOf(K(fNum)) + 1 && WidthsKept(), "«Создать новое свойство» после числа — сразу справа от него");

        var fRelNew = repo.AddField(new DbField
        {
            CollectionId = cid, Name = "Связь новая", FieldType = "relation", Position = 9002,
            ConfigJson   = new RelationConfig(cid, Single: false).ToConfigJson(),
        });
        table.AddColumnForField(fRelNew.Id!.Value); Pump();
        var ridA = Row(1).RecordId; var ridB = Row(2).RecordId;
        repo.SetLinks(fRelNew.Id!.Value, ridA, [ridB]);
        p.Rows!.RefreshLinks(ridA);
        Expect(p.RelationLinks.TryGet(fRelNew.Id!.Value, ridA, out var links) && links.Count == 1,
               "новая колонка-связь: после правки связи ячейка видит ее (список знает новое поле)");

        var copy = repo.DuplicateField(fStat.Id!.Value);
        var order = table.MovableKeysInOrder();
        order.Insert(order.IndexOf(K(fStat)) + 1, K(copy));
        repo.SaveColumnOrder(cid, order.Distinct().ToList());
        var keepV = table.CurrentVerticalOffset();
        table.ReloadColumns(); table.ReloadRows(); table.RestoreVerticalOffset(keepV);
        Settled();
        Expect(L().IndexOf(K(copy)) == L().IndexOf(K(fStat)) + 1 && WidthsKept(), "дубль свойства — справа от исходного, ширины на месте");
        Expect(Head(10).All(r => r[K(copy)] == r[K(fStat)]), "у копии значения исходного");

        var fNewDef = repo.GetFields(cid).First(f => f.Id == fNew.Id);
        fNewDef.FieldType = "number";
        repo.UpdateField(fNewDef);
        table.RebuildColumn(K(fNew)); Pump();
        Expect(defs.First(dd => dd.Key == K(fNew)).FieldType == "number" && WidthsKept(), "смена типа: колонка другого типа, ширины соседей те же");

        var copyCounts = repo.GetColumnCountsAsync(cid, K(copy)).Result.Where(c => c.Value != "").OrderByDescending(c => c.Count).First();
        Keep(K(copy), copyCounts.Value);
        Expect(s.IsColumnFiltered(K(copy)) && DataRows() == copyCounts.Count, "отбор по копии");
        Expect(WidthsKept(), "ручные ширины при отборе не сбросились");
        var rowsBeforeDelete = p.Rows;
        repo.DeleteField(copy.Id!.Value);
        table.ReloadColumns(); Pump();
        Settled();
        Expect(L().IndexOf(K(copy)) < 0 && WidthsKept(), "удаление колонки: ее нет, ширины остальных на месте");
        Expect(!ReferenceEquals(p.Rows, rowsBeforeDelete) && p.Rows!.Source is PlainRowSource && DataRows() == total,
               "отбор по удаленной колонке снят, строки пересчитаны");

        // Стандартная ширина столбцов из настроек — раскладка сразу, без пересборки строк. Настройка
        // принимает только значения из списка допустимых; берем самое большое, отличное от текущего:
        // с ним колонки заведомо шире окна и стоят ровно по настройке, а не растянуты поровну.
        table.SetColResizeMode(false); Pump();
        var rowsStd = p.Rows;
        var stdWas  = ConstructorSettings.ColWidth;
        var stdNew  = AppSettings.ConstructorColWidths.Where(v => v != stdWas).Max();
        ConstructorSettings.SetColWidth(stdNew); Pump();
        var movable = Enumerable.Range(0, L().Count).Where(i => L()[i].IsStarWidth).ToList();
        Expect(movable.All(i => Math.Abs(L().Width(i) - stdNew) < 0.51) && ReferenceEquals(p.Rows, rowsStd),
               $"стандартная ширина столбцов из настроек ({stdNew}): {L().Width(movable[0])} у всех, строки те же");
        ConstructorSettings.SetColWidth(stdWas); Pump();
        Expect(Math.Abs(L().Width(movable[0]) - stdNew) > 0.51, "возврат стандартной ширины — раскладка пересобрана");

        // ── Операции с записями ────────────────────────────────────────────────
        Console.WriteLine("Записи:");
        var rowsR = p.Rows;
        // Создание — как у контроллера: AddRecordIsolated без «после кого» ставит запись в конец.
        var added = repo.AddRecordIsolated(new DbRecord { CollectionId = cid, Title = "Созданная" }, null);
        table.ReloadRowsStructural(); Pump();
        total = repo.CountRecords(cid);
        Expect(ReferenceEquals(p.Rows, rowsR) && DataRows() == total && !p.Rows!.CountIsEstimate, $"создание в конце: список тот же, строк {DataRows():N0}");
        // Ранг незагруженной записи считается в фоне — прокрутка приходит с задержкой.
        table.ScrollToRecord(added.Id!.Value);
        bool LastIsAdded() => p.VisibleRowRange().Last == total && p.Rows!.TryGetCached(total - 1)?.RecordId == added.Id;
        WaitUntil(LastIsAdded, TimeSpan.FromSeconds(10));
        Expect(LastIsAdded(), $"прокрутка к созданной записи — видна последней ({p.VisibleRowRange().Label})");

        Sort(K(fNum), "По убыванию");
        var added2 = repo.AddRecordIsolated(new DbRecord { CollectionId = cid, Title = "При сортировке" }, null);
        table.ReloadRowsStructural();
        table.ScrollToRecord(added2.Id!.Value);
        Settled();
        var idx2 = p.Rows!.Source is IdListRowSource list2 ? list2.Ids.ToList().IndexOf(added2.Id!.Value) : -1;
        var (vr0, vr1) = s.VisibleRows();
        Expect(idx2 >= 0 && idx2 >= vr0 && idx2 <= vr1, $"создание при сортировке: список пересчитан, прокрутка дождалась его (строка {idx2 + 1})");
        Click(ToWindow(new Point(L().Left(L().IndexOf(num)) + 10, s.HeaderHeight + s.RowLayout.Top(3) - s.OffsetY + 10)), button: MouseButton.Right);
        Expect(Item(table.LastOpenedMenu, "Сдвинуть вниз") is null or { IsVisible: false }, "при сортировке сдвига записи в меню нет");
        table.LastOpenedMenu?.Close(); Pump();
        ClearFilter(K(fNum));
        total = repo.CountRecords(cid);

        var rid3 = Row(3).RecordId;
        Click(ToWindow(new Point(L().Left(L().IndexOf(num)) + 10, s.HeaderHeight + s.RowLayout.Top(3) - s.OffsetY + 10)), button: MouseButton.Right);
        var menu3 = table.LastOpenedMenu;
        Choose(Item(menu3, "Сдвинуть вниз"));
        menu3?.Close(); Pump();
        Expect(Row(4).RecordId == rid3 && p.SelectedRecordIds.SequenceEqual([rid3]), "меню «№» «Сдвинуть вниз» — запись ниже, выделена она");
        var rid6 = Row(6).RecordId;
        Click(Center(6, K(fText)));
        Gesture("table.moveRecordUp");
        Expect(Row(5).RecordId == rid6 && table.CurrentCellRange is { AnchorRow: 5 }, "сдвиг клавишами от ячейки — запись выше, рамка едет за ней");

        // Мультивыделение по «№»: удаление, дубль, «Расположение», «Действия» — весь набор.
        Point NumAt(int row) => ToWindow(new Point(L().Left(L().IndexOf(num)) + 10, s.HeaderHeight + s.RowLayout.Top(row) - s.OffsetY + 10));
        Click(NumAt(2));
        Click(NumAt(4), RawInputModifiers.Control);
        Click(NumAt(7), RawInputModifiers.Control);
        var set = new[] { Row(2).RecordId, Row(4).RecordId, Row(7).RecordId };
        Click(NumAt(4), button: MouseButton.Right);
        var menuR = table.LastOpenedMenu;
        // Пункты, которые меню пересчитывает при открытии: у рисованной сетки их зовет ее путь открытия.
        Expect(Item(menuR, "Действия") is { IsVisible: true } && Item(menuR, "Изменить цвет") is { ItemCount: 2 },
               "меню записи: «Действия» и цвета собраны при открытии");
        Choose(Item(menuR, "Удалить"));
        Choose(Item(menuR, "Дублировать"));
        Choose(Item(menuR, "Расположение"));
        var actions = Item(menuR, "Действия");
        Choose(actions?.ItemsSource is IEnumerable<MenuItem> sub ? sub.FirstOrDefault() : null);
        menuR?.Close(); Pump();
        Expect(deleted.LastOrDefault()?.SequenceEqual(set) == true, "«Удалить» из меню — весь выделенный набор");
        Expect(duplicated.LastOrDefault()?.SequenceEqual(set) == true, "«Дублировать» — весь набор");
        Expect(relocated.LastOrDefault()?.SequenceEqual(set) == true, "«Расположение» — весь набор");
        Expect(acted.LastOrDefault()?.SequenceEqual(set) == true, "«Действия» — обмен получил весь набор");
        Gesture("table.deleteRecords");
        Expect(deleted.Count == 2 && deleted[1].SequenceEqual(set), "Delete — тот же набор");

        // «Отображение записей»: скрытая запись уходит из строк и списков фильтра, прокрутка на месте.
        s.OffsetY = s.RowPitch * 20; Pump();
        var y0 = s.OffsetY;
        var hiddenRid = Row(25).RecordId;
        repo.SaveRecordDisplay(cid, [(hiddenRid, false, 1)]);
        table.ReloadRows();
        Settled();
        Expect(p.HasHiddenRecords && DataRows() == total - 1 && Math.Abs(s.OffsetY - y0) < 0.5,
               $"скрыта запись: строк {DataRows():N0}, прокрутка на месте");
        var flHidden = OpenFilter(K(fSel))!;
        Expect(Values(flHidden).Sum(Count) == total - 1, "в списке фильтра значения скрытой записи не считаются");
        flHidden.Hide(); Pump();
        repo.SetFieldValue(Row(26).RecordId, fText.Id!.Value, "из панели записи");
        table.RefreshRecordRow(Row(26).RecordId); Pump();
        Expect(Row(26)[K(fText)] == "из панели записи", "обновление строки из панели записи");

        w.Close();
        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "Все проверки прошли." : $"ПРОВАЛОВ: {failed}");
        return failed == 0 ? 0 : 3;
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static void Settle(Window w)
    {
        for (var i = 0; i < 50; i++)
        {
            w.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            if (Math.Abs(w.ClientSize.Width - w.Width) < 1 && Math.Abs(w.ClientSize.Height - w.Height) < 1) break;
        }
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }
}
