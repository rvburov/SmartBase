using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SmartBase.Controls.Dialogs;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Notes;
using SmartBase.Utils;

namespace SmartBase.Controls.Components;

/// <summary>Заметка переехала: старый и новый путь относительно хранилища.</summary>
public sealed record NoteMove(string OldPath, string NewPath);

/// <summary>
/// Дерево файлов хранилища заметок. Третий сайдбар приложения рядом с <c>SidebarTree</c>
/// (коллекции) и <c>SidebarList</c> (разделы). Отдельный контрол, а не режим SidebarTree: там ключ
/// узла — пара (DbId, Id), все ходит в ConstructorRepository и половина кода — права, мультибаза и
/// импорт, которых у файлов нет. Общее с ним — ВИД: разметка строки, отступы, линии и тулбар
/// скопированы один в один.
///
/// Обход диска ленивый: сразу читается корень, папка дочитывается при раскрытии. Полный обход
/// делает только кнопка «раскрыть все» — и то с потолком (см. <see cref="VaultScanner"/>).
/// </summary>
public partial class SidebarFiles : UserControl
{
    /// <summary>Выбрана заметка (.md) — относительный путь.</summary>
    public event EventHandler<string>? NoteSelected;
    /// <summary>Выбран файл, который в разделе не открывается, — имя для подсказки.</summary>
    public event EventHandler<string>? UnsupportedSelected;
    /// <summary>Заметка переименована или перенесена — открытую нужно переоткрыть по новому пути.</summary>
    public event EventHandler<NoteMove>? NoteMoved;
    /// <summary>Заметка (или папка с ней) удалена — относительный путь удаленного узла.</summary>
    public event EventHandler<string>? NoteRemoved;
    /// <summary>Состав или доступность хранилища изменились — разделу пора пересмотреть свое состояние.</summary>
    public event EventHandler? VaultChanged;

    private string _vault = "";
    private bool   _descending;
    private bool   _expandedAll;

    private List<NoteNode> _roots = [];
    private readonly ObservableCollection<SidebarFileRow> _rows = [];

    // Раскрытые папки и выделение живут вне узлов: дерево пересобирается при каждом Refresh,
    // а состояние должно его пережить. Ключ — относительный путь; сравнение — как в самой ФС
    // (на Linux с регистром, иначе без), иначе «Работа» и «работа» разъедутся.
    private readonly HashSet<string> _expanded = new(VaultRules.PathComparer);
    private readonly HashSet<string> _selected = new(VaultRules.PathComparer);

    /// <summary>Открытая в разделе заметка — подсветка переживает Refresh.</summary>
    private string? _open;

    // Был ли последний клик мышью внутри сайдбара: Delete удаляет выделение только тогда —
    // иначе клавиша принадлежит редактору справа.
    private bool _lastPressInSidebar;
    private TopLevel? _keyHookTopLevel;

    // Дельта последнего рендера — чтобы перерисовать при возврате, если размер шрифта меняли,
    // пока сайдбар был не виден.
    private int _renderedFontDelta = FontScale.Delta;

    private Window? OwnerWindow => VisualRoot as Window;

    public SidebarFiles()
    {
        InitializeComponent();
        RowsHost.ItemsSource = _rows;

        _descending = AppSettings.LoadNotesSort() == "desc";
        ApplySortIcon();

        CreateBtn.Click            += (_, _) => _ = CreateItem(TargetFolderForCreate(), NoteKind.File);
        ExpandBtn.IsCheckedChanged += (_, _) => ToggleExpandAll(ExpandBtn.IsChecked == true);
        SortBtn.Click              += (_, _) => ToggleSort();
    }

    // ── Хранилище ───────────────────────────────────────────────────────────────

    /// <summary>Задать (или снять) папку хранилища. Пустой путь — раздел выключен, диск не читаем.</summary>
    public void SetVault(string? path)
    {
        var next = (path ?? "").Trim();
        if (string.Equals(next, _vault, StringComparison.Ordinal)) return;

        _vault = next;
        _expanded.Clear();
        _selected.Clear();
        _open = null;
        _expandedAll = false;
        ExpandBtn.IsChecked = false;

        Refresh();
    }

    public bool HasVault      => _vault.Length > 0;
    public bool VaultIsMissing => HasVault && !Directory.Exists(_vault);

    /// <summary>Абсолютный путь по относительному — разделу он нужен для чтения и записи файла.</summary>
    public string FullPathOf(string relativePath) => VaultScanner.FullPathOf(_vault, relativePath);

    /// <summary>Перечитать дерево с диска, сохранив раскрытые ветки и подсветку.</summary>
    public void Refresh()
    {
        _renderedFontDelta = FontScale.Delta;

        if (!HasVault || VaultIsMissing)
        {
            _roots = [];
            RebuildRows();
            return;
        }

        _roots = VaultScanner.ScanChildren(_vault, "");
        VaultRules.SortLevel(_roots, _descending);
        LoadExpanded(_roots);
        RebuildRows();
    }

    /// <summary>Раскрыть ветку до заметки и подсветить ее — после создания или переноса.</summary>
    public void Reveal(string relativePath)
    {
        var rel = VaultRules.Normalize(relativePath);
        if (rel.Length == 0) return;

        // Раскрываем всех предков, иначе строка не попадет в плоский список.
        var parent = VaultRules.ParentOf(rel);
        while (parent.Length > 0)
        {
            _expanded.Add(parent);
            parent = VaultRules.ParentOf(parent);
        }

        Refresh();
        _selected.Clear();
        _selected.Add(rel);
        _open = rel;
        RebuildRows();
    }

    /// <summary>Подсветить открытую заметку (раздел восстановил ее из настроек).</summary>
    public void SetOpenNote(string? relativePath)
    {
        _open = string.IsNullOrEmpty(relativePath) ? null : VaultRules.Normalize(relativePath);
        _selected.Clear();
        if (_open is not null) _selected.Add(_open);
        RebuildRows();
    }

    // Дочитывает детей у раскрытых папок. Ленивый обход означает, что после Refresh дерево
    // знает только корень, — без этого раскрытая ветка схлопнулась бы.
    private void LoadExpanded(List<NoteNode> nodes)
    {
        foreach (var n in nodes.Where(n => n.IsFolder && _expanded.Contains(n.RelativePath)))
        {
            n.IsExpanded = true;
            if (!n.IsLoaded)
            {
                n.Children.AddRange(VaultScanner.ScanChildren(_vault, n.RelativePath));
                VaultRules.SortLevel(n.Children, _descending);
                n.IsLoaded = true;
            }
            LoadExpanded(n.Children);
        }
    }

    // ── Плоский список видимых строк ────────────────────────────────────────────

    private void RebuildRows()
    {
        _rows.Clear();
        Flatten(_roots, 0);
        // Первая строка стоит на месте шапки колонок таблицы (высота 36), остальные — на месте
        // строк данных (36 + линия). См. SidebarFileRow.IsFirstRow.
        if (_rows.Count > 0) _rows[0].IsFirstRow = true;
        MeasureRowWidth();
        UpdateEmptyHint();
    }

    private void Flatten(IEnumerable<NoteNode> nodes, int depth)
    {
        foreach (var n in nodes)
        {
            _rows.Add(new SidebarFileRow(n, depth, _selected.Contains(n.RelativePath)));
            if (n.IsFolder && n.IsExpanded) Flatten(n.Children, depth + 1);
        }
    }

    private void UpdateEmptyHint()
    {
        EmptyHint.Text = !HasVault      ? "Папка хранилища не выбрана"
                       : VaultIsMissing ? "Папка хранилища недоступна"
                       :                  "В этой папке пока нет файлов";
        EmptyHint.IsVisible = _rows.Count == 0;
    }

    // Мерить каждую строку нельзя — узлов бывают тысячи. Сначала отбираем кандидатов дешево
    // (отступ + число символов), потом честно меряем только их: шрифт пропорциональный, и
    // самое длинное имя не обязательно самое широкое.
    private const int    WidthCandidates = 16;
    private const double WidthSlack      = 4;

    /// <summary>
    /// Общая ширина строк — по самой широкой из них. Ставится КАЖДОЙ строке, поэтому экстент
    /// горизонтальной прокрутки не зависит от того, какие строки сейчас реализованы
    /// (виртуализация), и полоса внизу не дергается при вертикальной прокрутке.
    /// </summary>
    private void MeasureRowWidth()
    {
        if (_rows.Count == 0) return;

        var fontSize = Metric("FontSizeBase",   14);
        var iconSize = Metric("IconSizeMedium", 20);
        var family   = this.TryFindResource("FontFamilyBase", out var f) ? f as FontFamily : null;

        // Все, что стоит СЛЕВА от текста, по шаблону строки: Padding строки (10), отступ
        // вложенности, слоты стрелки и чекбокса, иконка с ее отступом (2) и поля текста (6 и 8).
        double Lead(SidebarFileRow r) =>
            10 + r.IndentMargin.Left + r.ChevronSlot + r.CheckboxSlot + 2 + iconSize + 6 + 8;

        var max = 0.0;
        foreach (var r in _rows
                     .OrderByDescending(r => Lead(r) + r.Name.Length)
                     .Take(WidthCandidates))
            max = Math.Max(max, Lead(r) + Probe(r.Name, fontSize, family));

        var width = Math.Ceiling(max) + WidthSlack;
        foreach (var r in _rows) r.MinRowWidth = width;
    }

    private double Metric(string key, double fallback) =>
        this.TryFindResource(key, out var v) && v is double d ? d : fallback;

    private static double Probe(string text, double fontSize, FontFamily? family)
    {
        var probe = new TextBlock { Text = text, FontSize = fontSize };
        if (family is not null) probe.FontFamily = family;
        probe.Measure(Size.Infinity);
        return probe.DesiredSize.Width;
    }

    // ── Раскрытие, сворачивание, сортировка ─────────────────────────────────────

    private void ToggleExpandAll(bool expand)
    {
        _expandedAll = expand;
        ToolTip.SetTip(ExpandBtn, expand ? "Свернуть все" : "Развернуть все");
        ExpandIcon.IsVisible   = !expand;
        CollapseIcon.IsVisible = expand;

        if (!expand)
        {
            _expanded.Clear();
            Refresh();
            return;
        }

        if (!HasVault || VaultIsMissing) return;

        // Единственное место, где хранилище читается целиком, — и то с потолком. Если уперлись
        // в него, говорим об этом вслух: молча показать половину дерева значило бы соврать.
        var (roots, truncated) = VaultScanner.ScanAll(_vault, _descending);
        _roots = roots;
        _expanded.Clear();
        MarkExpanded(_roots);
        RebuildRows();

        if (truncated)
            Toast.Error(ExpandBtn,
                $"Хранилище очень большое — показаны первые {VaultScanner.MaxNodes} элементов.");
    }

    private void MarkExpanded(IEnumerable<NoteNode> nodes)
    {
        foreach (var n in nodes.Where(n => n.IsFolder))
        {
            n.IsExpanded = true;
            _expanded.Add(n.RelativePath);
            MarkExpanded(n.Children);
        }
    }

    // Положений два, а не три, как в конструкторе: у файлов нет собственной позиции, а порядок
    // файловой системы произволен — «без сортировки» показывать нечего.
    private void ToggleSort()
    {
        _descending = !_descending;
        AppSettings.SaveNotesSort(_descending ? "desc" : "asc");
        ApplySortIcon();
        Refresh();
    }

    private void ApplySortIcon()
    {
        SortAscIcon.IsVisible  = !_descending;
        SortDescIcon.IsVisible = _descending;
        ToolTip.SetTip(SortBtn, _descending ? "Я → А" : "А → Я");
    }

    // ── Ввод ────────────────────────────────────────────────────────────────────

    private void OnChevronTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: SidebarFileRow row }) return;
        ToggleFolder(row);
        e.Handled = true;   // не открывать заодно и содержимое
    }

    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: SidebarFileRow row }) return;
        var node = row.Node;

        // Shift/Ctrl/Cmd — точечный набор для пакетных операций (перенос, дубль, удаление).
        var additive = e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                    || e.KeyModifiers.HasFlag(KeyModifiers.Control)
                    || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (additive)
        {
            if (!_selected.Remove(node.RelativePath)) _selected.Add(node.RelativePath);
            RebuildRows();
            return;
        }

        if (node.IsFolder)
        {
            ToggleFolder(row);
            return;
        }

        _selected.Clear();
        _selected.Add(node.RelativePath);

        if (node.IsMarkdown)
        {
            _open = node.RelativePath;
            NoteSelected?.Invoke(this, node.RelativePath);
        }
        else
        {
            // Файл виден, но открыть его в заметках нельзя. Подсветку оставляем — по ней видно,
            // на что человек нажал, — а раздел показывает пояснение вместо содержимого.
            UnsupportedSelected?.Invoke(this, node.Name);
        }

        RebuildRows();
    }

    private void ToggleFolder(SidebarFileRow row)
    {
        var node = row.Node;
        if (!node.IsFolder) return;

        node.IsExpanded = !node.IsExpanded;
        if (node.IsExpanded)
        {
            _expanded.Add(node.RelativePath);
            if (!node.IsLoaded)
            {
                node.Children.AddRange(VaultScanner.ScanChildren(_vault, node.RelativePath));
                VaultRules.SortLevel(node.Children, _descending);
                node.IsLoaded = true;
            }
        }
        else _expanded.Remove(node.RelativePath);

        row.NotifyExpanded();
        RebuildRows();
    }

    private void OnRowEnter(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: SidebarFileRow row }) row.SetHovered(true);
    }

    private void OnRowExit(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: SidebarFileRow row }) row.SetHovered(false);
    }

    // ── Контекстное меню ────────────────────────────────────────────────────────

    private void OnRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: SidebarFileRow row } anchor) return;
        if (!HasVault || VaultIsMissing) return;

        var node = row.Node;

        // Правый клик вне набора — работаем с одним узлом под курсором (как в конструкторе).
        if (!_selected.Contains(node.RelativePath))
        {
            _selected.Clear();
            _selected.Add(node.RelativePath);
            RebuildRows();
        }
        var multi = _selected.Count > 1;

        var menu = new ContextMenu();

        if (node.IsFolder)
        {
            menu.Items.Add(Item(MenuIcons.CreateFolderHeader(), () => _ = CreateItem(node.RelativePath, NoteKind.Folder)));
            menu.Items.Add(Item(MenuIcons.CreateNoteHeader(),   () => _ = CreateItem(node.RelativePath, NoteKind.File)));
            menu.Items.Add(new Separator());
        }

        if (!multi) menu.Items.Add(Item(MenuIcons.RenameHeader(), () => _ = RenameNode(node)));
        menu.Items.Add(Item(MenuIcons.LocationHeader(),  () => _ = MoveSelection(node)));
        menu.Items.Add(Item(MenuIcons.DuplicateHeader(), () => _ = DuplicateSelection(node)));
        menu.Items.Add(Item(MenuIcons.DeleteHeader(),    () => _ = DeleteSelection(node)));

        menu.Open(anchor);
        e.Handled = true;
    }

    private void OnEmptyContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (!HasVault || VaultIsMissing) return;
        // Строку меню поймала сама — здесь остается только пустое место списка.
        if (e.Source is Visual v && v.FindAncestorOfType<Border>() is { Name: "RowRoot" }) return;

        _selected.Clear();
        RebuildRows();

        var menu = new ContextMenu();
        menu.Items.Add(Item(MenuIcons.CreateFolderHeader(), () => _ = CreateItem("", NoteKind.Folder)));
        menu.Items.Add(Item(MenuIcons.CreateNoteHeader(),   () => _ = CreateItem("", NoteKind.File)));
        menu.Open(this);
        e.Handled = true;
    }

    private static MenuItem Item(Control header, Action run)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => run();
        return item;
    }

    // ── Операции ────────────────────────────────────────────────────────────────

    // Куда кладет кнопка «+»: в выделенную папку, в папку выделенной заметки, иначе в корень.
    private string TargetFolderForCreate()
    {
        if (_selected.Count != 1) return "";
        var rel  = _selected.First();
        var node = FindNode(_roots, rel);
        return node is null ? "" : node.IsFolder ? node.RelativePath : VaultRules.ParentOf(rel);
    }

    private async Task CreateItem(string parentRelative, NoteKind presetKind)
    {
        if (!Ready()) return;

        // Тултип гасим руками — иначе он всплывает поверх модального окна.
        ToolTip.SetIsOpen(CreateBtn, false);

        var folders = VaultScanner.ScanFolders(_vault, _descending);
        var dlg     = NoteItemDialog.ForCreate(folders, parentRelative, presetKind);
        var res     = OwnerWindow is null ? null : await dlg.ShowDialog<NoteItemResult?>(OwnerWindow);
        if (res is null) return;

        var parentFull = VaultScanner.FullPathOf(_vault, res.ParentRelativePath);
        var result = res.Kind == NoteKind.Folder
            ? VaultOperations.CreateFolder(parentFull, res.Name)
            : VaultOperations.CreateNote(parentFull, res.Name);

        if (!Applied(result, CreateBtn)) return;

        var created = VaultRules.Combine(res.ParentRelativePath, Path.GetFileName(result.Path!));
        Reveal(created);
        VaultChanged?.Invoke(this, EventArgs.Empty);

        // Новая заметка открывается сразу — иначе после создания нужно еще раз кликать по ней.
        if (res.Kind == NoteKind.File)
        {
            _open = created;
            NoteSelected?.Invoke(this, created);
        }
    }

    private async Task RenameNode(NoteNode node)
    {
        if (!Ready() || OwnerWindow is null) return;

        var res = await NoteItemDialog.ForRename(node).ShowDialog<NoteItemResult?>(OwnerWindow);
        if (res is null) return;

        if (ApplyRename(node, res.Name) is { } error) Toast.Error(this, error);
    }

    /// <summary>
    /// Переименование, пришедшее из шапки раздела: та же операция. Возвращает текст ошибки —
    /// показать ее должен тот, кто спрашивал имя, у своего поля, а не сайдбар у себя.
    /// </summary>
    public string? RenameFromHeader(string relativePath, string newName)
    {
        var node = FindNode(_roots, relativePath);
        return node is null ? "Заметка не найдена — обновите дерево." : ApplyRename(node, newName);
    }

    private string? ApplyRename(NoteNode node, string newName)
    {
        var result = VaultOperations.Rename(node.FullPath, newName, node.IsFolder);
        if (!result.Ok) return result.Error ?? "Не удалось переименовать.";

        var oldRel = node.RelativePath;
        var newRel = VaultRules.Combine(VaultRules.ParentOf(oldRel), Path.GetFileName(result.Path!));
        RemapState(oldRel, newRel);

        Refresh();
        NoteMoved?.Invoke(this, new NoteMove(oldRel, newRel));
        VaultChanged?.Invoke(this, EventArgs.Empty);
        return null;
    }

    private async Task MoveSelection(NoteNode anchorNode)
    {
        if (!Ready() || OwnerWindow is null) return;

        var nodes = SelectedNodes(anchorNode);
        // Поддерево переносимой папки целью не предлагаем — иначе она исчезла бы внутри себя.
        // При наборе из нескольких папок список исключает поддерево якорной, а остальные
        // случаи ловит сама операция (Move отказывается класть папку внутрь себя).
        var folders = VaultScanner.ScanFolders(_vault, _descending);

        var res = await NoteItemDialog.ForMove(folders, anchorNode).ShowDialog<NoteItemResult?>(OwnerWindow);
        if (res is null) return;

        var targetFull = VaultScanner.FullPathOf(_vault, res.ParentRelativePath);
        var renamed = 0;
        foreach (var node in nodes)
        {
            var result = VaultOperations.Move(node.FullPath, targetFull, node.IsFolder);
            if (!Applied(result, this)) return;

            var newRel = VaultRules.Combine(res.ParentRelativePath, Path.GetFileName(result.Path!));
            if (!string.Equals(Path.GetFileName(result.Path!), node.Name, StringComparison.Ordinal)) renamed++;
            RemapState(node.RelativePath, newRel);
            NoteMoved?.Invoke(this, new NoteMove(node.RelativePath, newRel));
        }

        Refresh();
        VaultChanged?.Invoke(this, EventArgs.Empty);

        // Имя в целевой папке было занято — сказать об этом, иначе вещь «пропадет» под другим именем.
        if (renamed > 0) Toast.Error(this, "Имя в целевой папке было занято — добавлено «(копия)».");
    }

    // Порог показа окна прогресса. Копирование сотни файлов — доли секунды, ради них окно мигать
    // не должно; тысячи — уже заметная пауза, и молчаливая заморозка читалась бы как зависание.
    private const int BulkProgressThreshold = 500;

    private async Task DuplicateSelection(NoteNode anchorNode)
    {
        if (!Ready()) return;

        var nodes = SelectedNodes(anchorNode);
        var total = nodes.Where(n => n.IsFolder)
                         .Sum(n => VaultOperations.CountEntries(n.FullPath, BulkProgressThreshold + 1));

        // Малый объем — сразу, без окна.
        if (total <= BulkProgressThreshold || OwnerWindow is null)
        {
            foreach (var node in nodes)
                if (!Applied(VaultOperations.Duplicate(node.FullPath, node.IsFolder), this)) return;

            Refresh();
            VaultChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Большая папка — окно прогресса, копирование в фоне (как дубль коллекции в конструкторе).
        var dlg = new ProgressDialog { Title = "Дублирование" };
        dlg.SetInfo($"Копируется больше {BulkProgressThreshold} файлов.");
        dlg.SetProgress(0, "Дублирование…");
        var dlgClosed = dlg.ShowDialog(OwnerWindow);

        string? error = null;
        var lastPct = -1;
        try
        {
            await Task.Run(() =>
            {
                IProgress<int> p = new Progress<int>(copied =>
                {
                    var pct = Math.Min(100, (int)(copied * 100L / Math.Max(1, total)));
                    if (pct == lastPct) return;
                    lastPct = pct;
                    Dispatcher.UIThread.Post(() => dlg.SetProgress(pct / 100.0, "Дублирование…"));
                });

                foreach (var node in nodes)
                {
                    var result = VaultOperations.Duplicate(node.FullPath, node.IsFolder, p);
                    if (result.Ok) continue;
                    error = result.Error;
                    return;
                }
            });
        }
        catch (Exception ex)
        {
            error = VaultErrors.Describe(ex, _vault);
        }

        dlg.Done();
        await dlgClosed;

        if (error is not null) Toast.Error(this, error);
        Refresh();
        VaultChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task DeleteSelection(NoteNode anchorNode)
    {
        if (!Ready() || OwnerWindow is null) return;

        var nodes = SelectedNodes(anchorNode);
        if (await new ConfirmDialog(DeleteMessage(nodes), "Удалить").ShowDialog<bool>(OwnerWindow) != true) return;

        foreach (var node in nodes)
        {
            var result = VaultOperations.Delete(node.FullPath);

            // Корзина недоступна (сетевой диск, урезанные права) — спрашиваем отдельно.
            // Безвозвратно стирать чужие файлы без второго «да» нельзя.
            if (!result.Ok)
            {
                var name = node.Name;
                var ok = await new ConfirmDialog(
                        $"Не удалось переместить «{name}» в корзину.\n" +
                        "Удалить безвозвратно? Восстановить будет нельзя.",
                        "Удалить безвозвратно")
                    .ShowDialog<bool>(OwnerWindow);
                if (!ok) continue;

                result = VaultOperations.DeletePermanently(node.FullPath, node.IsFolder);
                if (!Applied(result, this)) return;
            }

            ForgetState(node.RelativePath);
            NoteRemoved?.Invoke(this, node.RelativePath);
        }

        _selected.Clear();
        Refresh();
        VaultChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string DeleteMessage(List<NoteNode> nodes)
    {
        if (nodes.Count == 1)
            return nodes[0].IsFolder
                ? $"Переместить папку «{nodes[0].Name}» в корзину? Вложенные папки и файлы уйдут вместе с ней."
                : $"Переместить «{nodes[0].Name}» в корзину?";

        var folders = nodes.Count(n => n.IsFolder);
        return folders > 0
            ? $"Переместить в корзину выбранное ({nodes.Count})? Вложенные папки и файлы уйдут вместе с ними."
            : $"Переместить в корзину выбранное ({nodes.Count})?";
    }

    // ── Состояние ───────────────────────────────────────────────────────────────

    // Узлы под операцию: весь набор, если клик пришелся по выделенному, иначе один узел.
    private List<NoteNode> SelectedNodes(NoteNode anchorNode)
    {
        if (!_selected.Contains(anchorNode.RelativePath)) return [anchorNode];

        var found = _selected.Select(rel => FindNode(_roots, rel)).OfType<NoteNode>().ToList();
        return found.Count > 0 ? found : [anchorNode];
    }

    private static NoteNode? FindNode(IEnumerable<NoteNode> nodes, string relativePath)
    {
        var rel = VaultRules.Normalize(relativePath);
        foreach (var n in nodes)
        {
            if (n.RelativePath.Equals(rel, VaultRules.Comparison)) return n;
            if (n.IsFolder && VaultRules.IsInside(rel, n.RelativePath)
                && FindNode(n.Children, rel) is { } found) return found;
        }
        return null;
    }

    // Переименовали или перенесли папку — раскрытые ветки и выделение под ней должны переехать
    // вместе с ней, иначе вся ветка схлопнется прямо во время работы.
    private void RemapState(string oldRel, string newRel)
    {
        Rebase(_expanded);
        Rebase(_selected);
        if (_open is not null) _open = VaultRules.Rebase(_open, oldRel, newRel);

        void Rebase(HashSet<string> set)
        {
            var moved = set.Where(k => VaultRules.IsInside(k, oldRel)).ToList();
            foreach (var k in moved)
            {
                set.Remove(k);
                set.Add(VaultRules.Rebase(k, oldRel, newRel));
            }
        }
    }

    private void ForgetState(string rel)
    {
        _expanded.RemoveWhere(k => VaultRules.IsInside(k, rel));
        _selected.RemoveWhere(k => VaultRules.IsInside(k, rel));
        if (_open is not null && VaultRules.IsInside(_open, rel)) _open = null;
    }

    // Хранилище на месте? Иначе операция бессмысленна, и об этом надо сказать, а не молчать.
    private bool Ready()
    {
        if (HasVault && !VaultIsMissing) return true;

        Toast.Error(CreateBtn, HasVault
            ? "Папка хранилища недоступна. Проверьте ее в «Настройки → Заметки»."
            : "Папка хранилища не выбрана. Задайте ее в «Настройки → Заметки».");
        return false;
    }

    private bool Applied(VaultResult result, Control anchor)
    {
        if (result.Ok) return true;
        Toast.Error(anchor, result.Error ?? "Не удалось выполнить действие.");
        return false;
    }

    // ── Клавиатура и шрифт ──────────────────────────────────────────────────────

    private void OnFontScaleChanged() => Refresh();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _keyHookTopLevel = TopLevel.GetTopLevel(this);
        // Клавиши и клики ловим на уровне окна (туннель): фокус UserControl ненадежен.
        _keyHookTopLevel?.AddHandler(KeyDownEvent,        OnTopLevelKeyDown,        RoutingStrategies.Tunnel);
        _keyHookTopLevel?.AddHandler(PointerPressedEvent, OnTopLevelPointerPressed, RoutingStrategies.Tunnel);

        FontScale.Changed += OnFontScaleChanged;
        if (HasVault && _renderedFontDelta != FontScale.Delta) Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _keyHookTopLevel?.RemoveHandler(KeyDownEvent,        OnTopLevelKeyDown);
        _keyHookTopLevel?.RemoveHandler(PointerPressedEvent, OnTopLevelPointerPressed);
        _keyHookTopLevel = null;
        FontScale.Changed -= OnFontScaleChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTopLevelPointerPressed(object? sender, PointerPressedEventArgs e) =>
        _lastPressInSidebar = e.Source is Visual v && (ReferenceEquals(v, this) || this.IsVisualAncestorOf(v));

    // Delete удаляет выделение — тот же путь, что и пункт меню. Срабатывает, только если
    // последний клик был в этом сайдбаре: иначе клавиша принадлежит редактору справа (а в окне
    // такой же хук стоит у дерева конструктора).
    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Delete) return;
        if (!IsVisible || _selected.Count == 0) return;
        if (IsTextInputFocused(e)) return;
        if (!_lastPressInSidebar) return;

        var first = FindNode(_roots, _selected.First());
        if (first is null) return;

        _ = DeleteSelection(first);
        e.Handled = true;
    }

    private static bool IsTextInputFocused(KeyEventArgs e) =>
        e.Source is TextBox || (e.Source is Visual v && v.FindAncestorOfType<TextBox>() is not null);
}
