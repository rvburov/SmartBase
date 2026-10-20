using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace SmartBase.Controls.Widgets;

/// <summary>
/// Редактор свойства типа «файл». Значение — абсолютный путь либо список путей авто-привязки
/// (ничего не копируется). Показ: имена файлов отдельными ссылками (клик — открыть через ОС),
/// «имя — файл не найден», если пути нет, и серое «Выбрать файл…» для пустого значения.
/// Правка — по аналогии с датой: ручной ввод пути в поле + правая иконка-скрепка,
/// открывающая системный выбор файла. Неверный путь при ручном вводе — красный текст и
/// плашка-уведомление (<see cref="NoticeBanner"/>). Хранит/отдает путь через <see cref="Storage"/>.
/// </summary>
public partial class FileEditor : UserControl
{
    private string _path = "";        // хранимое значение: путь, список путей или ""
    private IReadOnlyList<string> _paths = [];
    private string? _pendingPath;      // путь именно той ссылки, по которой нажали
    private string _autoFolder = "";  // непусто → авто-режим (read-only, значения ведет правило)
    private bool   _editing;
    private bool   _suppress;
    private Window? _window;
    private readonly DispatcherTimer _clickTimer;
    private DispatcherTimer? _errFlash;

    /// <summary>Поднимается при изменении значения пользователем.</summary>
    public event EventHandler? Changed;

    /// <summary>Ручная правка пути кончилась (Enter, Escape, уход фокуса, выбор файла) — до того, как
    /// поле ввода спрячется: в обработчике еще видно, был ли фокус в редакторе.</summary>
    public event EventHandler? EditEnded;

    public FileEditor()
    {
        InitializeComponent();

        // Одиночный клик по имени файла обрабатываем с задержкой, чтобы двойной успел
        // его отменить (двойной — ручная правка пути, одиночный — открыть/поправить).
        _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer.Stop();
            var path = _pendingPath;
            _pendingPath = null;
            OnSingleClick(path);
        };

        Pick.Click += async (_, _) => await PickFileAsync();

        LinkHost.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(LinkHost).Properties.IsLeftButtonPressed) return;
            // У списка каждая ссылка обрабатывает свое нажатие; разделители и пустое место — нет.
            if (MultiLinks.IsVisible) return;
            e.Handled = true;
            // Авто-режим: ручной правки нет (двойной клик не редактирует), только одиночное действие.
            if (!IsAuto && e.ClickCount >= 2) { _clickTimer.Stop(); BeginEdit(); return; }
            QueueClick(null);
        };

        Input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)       CommitManual();
            else if (e.Key == Key.Escape) CancelEdit();
        };
        Input.LostFocus   += (_, _) => CommitManual();
        Input.TextChanged += (_, _) => { if (!_suppress) LiveValidate(); };

        // Пересчет показа после подключения к дереву: в конструкторе/инициализаторе (когда
        // задается Storage) ресурсы темы еще не резолвятся (this.TryFindResource → fallback),
        // из-за чего ссылка красилась не акцентом (#007AFF), а запасным цветом. На attach
        // BrushAccent/BrushSecondaryText уже доступны — перекрашиваем корректно.
        // Плюс подписываемся на окно: плашку-ошибку (top-level popup) надо прятать, когда
        // приложение теряет фокус, иначе она висит поверх других программ.
        AttachedToVisualTree += (_, _) =>
        {
            UpdateVisual();
            _window = TopLevel.GetTopLevel(this) as Window;
            if (_window is null) return;
            _window.Deactivated += OnWindowDeactivated;
            _window.Activated   += OnWindowActivated;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _clickTimer.Stop();
            HideError();
            if (_window is null) return;
            _window.Deactivated -= OnWindowDeactivated;
            _window.Activated   -= OnWindowActivated;
            _window = null;
        };

        UpdateVisual();
    }

    /// <summary>Путь или список путей для db_field_values. null/"" — файл не выбран.</summary>
    public string? Storage
    {
        get => _path;
        set
        {
            _path = value ?? "";
            _paths = Core.Models.FileValue.ReadPaths(_path);
            _clickTimer.Stop();
            _pendingPath = null;
            EndEdit();
            UpdateVisual();
        }
    }

    /// <summary>Папка авто-привязки. Непусто → ячейка read-only (правило ведет значения): скрепки
    /// и ручного ввода нет, недоступная папка показывается как «папка недоступна». Пусто — обычный
    /// ручной режим.</summary>
    public string AutoFolder
    {
        set
        {
            _autoFolder = value ?? "";
            _clickTimer.Stop();
            _pendingPath = null;
            Pick.IsVisible = _autoFolder.Length == 0;   // в авто-режиме скрепки нет
            EndEdit();
            UpdateVisual();
        }
    }

    private bool IsAuto => _autoFolder.Length > 0;

    /// <summary>Обновить показ из модели (async-дозагрузка строки таблицы / правка из другого
    /// места) — без поднятия <see cref="Changed"/> и не прерывая текущую ручную правку.</summary>
    public void SyncStorage(string? path)
    {
        if (_editing) return;                 // не мешаем ручному вводу
        var v = path ?? "";
        if (v == _path) return;               // значение не изменилось
        _path = v;
        _paths = Core.Models.FileValue.ReadPaths(_path);
        _clickTimer.Stop();
        _pendingPath = null;
        UpdateVisual();
    }

    /// <summary>Плоский режим: убрать собственную рамку/фон, чтобы редактор встраивался в
    /// карточку свойства (RecordPageView) и в ячейку таблицы без «бокса в боксе».</summary>
    public bool Flat
    {
        set
        {
            if (!value) return;
            Box.Classes.Remove("timeField");
            Box.Background      = Brushes.Transparent;
            Box.BorderThickness = default;
            Box.Height          = double.NaN;
            LinkHost.Padding    = new Thickness(0);
            Input.Padding       = new Thickness(0);
        }
    }

    /// <summary>Левый отступ значения (показ и ввод). В ячейке таблицы = 6px, чтобы текст
    /// выравнивался с остальными колонками; на карточке записи — 0 (задает карточка).</summary>
    public double ContentLeftPadding
    {
        set
        {
            LinkHost.Padding = new Thickness(value, 0, 0, 0);
            Input.Padding    = new Thickness(value, 0, 0, 0);
        }
    }

    // ── Клики/действия показа ───────────────────────────────────────────────────

    private void QueueClick(string? path)
    {
        _clickTimer.Stop();
        _pendingPath = path;
        _clickTimer.Start();
    }

    private void OnSingleClick(string? selectedPath)
    {
        var path = selectedPath ?? (_paths.Count == 1 ? _paths[0] : "");
        if (IsAuto)
        {
            // Авто-режим read-only: папка недоступна → плашка; файл есть → открыть; иначе — ничего.
            if (!System.IO.Directory.Exists(_autoFolder)) { FlashError("Папка недоступна"); return; }
            if (path.Length > 0 && File.Exists(path)) OpenWithSystem(path);
            return;
        }
        if (path.Length == 0) return;              // «Выбрать файл…» — не кликабельно
        if (File.Exists(path)) OpenWithSystem(path);
        else if (_paths.Count == 1) BeginEdit();    // одиночный отсутствующий путь можно поправить
    }

    /// <summary>Открыть файл штатным приложением ОС. UseShellExecute=true делегирует системе:
    /// open (macOS), explorer/ассоциация (Windows), xdg-open (Linux). Общее с ячейкой «Файла»
    /// рисованной таблицы — у нее нет своего контрола в ячейке.</summary>
    public static void OpenWithSystem(string path)
    {
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch (Exception ex)
        {
            // Нечем открыть / нет прав — для человека тихо, для журнала нет: «файл не открывается»
            // разбирается по этой записи (сетевой путь недоступен, ассоциации нет).
            Serilog.Log.Warning(ex, "Не удалось открыть файл системой: {Path:l}", path);
        }
    }

    /// <summary>Системный выбор одного файла; null — отказались. Общее с ячейкой «Файла» рисованной таблицы.</summary>
    public static async Task<string?> PickPathAsync(TopLevel top)
    {
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Выберите файл",
            AllowMultiple = false,
        });
        return files.Count == 0 ? null : files[0].Path.LocalPath;
    }

    private async Task PickFileAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        if (await PickPathAsync(top) is not { } picked) return;
        SetPath(picked, raise: true);
        EndEdit();
        UpdateVisual();
    }

    // ── Ручная правка пути ──────────────────────────────────────────────────────

    /// <summary>Начать ручную правку пути (двойной клик по имени, клик по отсутствующему файлу) —
    /// рисованная таблица ставит редактор над ячейкой и сразу открывает в нем поле ввода.</summary>
    public void BeginManualEdit() => BeginEdit();

    /// <summary>Записать набранный путь и закончить правку (Enter у таблицы, коммит правки ячейки).</summary>
    public void CommitManualEdit() => CommitManual();

    /// <summary>Закончить правку без записи (Escape у таблицы).</summary>
    public void CancelManualEdit() => CancelEdit();

    private void BeginEdit()
    {
        if (IsAuto || _paths.Count > 1) return;
        _editing = true;
        _suppress = true;
        Input.Text = _path;
        _suppress = false;
        LinkHost.IsVisible = false;
        Input.IsVisible    = true;
        Input.Focus();
        // Каретка в начало, без выделения: SelectAll уводил ее в конец, и поле прокручивалось
        // к хвосту пути — текст на входе в правку прыгал влево. Начало пути еще и полезнее:
        // видно, откуда файл.
        Input.SelectionStart = Input.SelectionEnd = Input.CaretIndex = 0;
        LiveValidate();
    }

    private void CommitManual()
    {
        if (!_editing) return;
        var text = Input.Text?.Trim() ?? "";
        SetPath(text, raise: text != _path);
        EndEdit();
        UpdateVisual();
    }

    private void CancelEdit()
    {
        EndEdit();
        UpdateVisual();
    }

    private void EndEdit()
    {
        // Флаг снимается ДО события: обработчик может снять редактор с экрана, поле потеряет фокус, и
        // его LostFocus (CommitManual) не должен начать вторую запись.
        var was = _editing;
        _editing = false;
        if (was) EditEnded?.Invoke(this, EventArgs.Empty);
        Input.IsVisible    = false;
        LinkHost.IsVisible = true;
        HideError();
    }

    // Живая проверка при ручном вводе: непустой путь без файла → красный текст + плашка,
    // которая держится, пока путь не станет верным (или поле не покинут).
    private void LiveValidate()
    {
        var text    = Input.Text?.Trim() ?? "";
        var invalid = text.Length > 0 && !File.Exists(text);
        Input.Foreground = invalid ? Red() : Primary();
        if (invalid) ShowError("Укажите путь и наименование файла");
        else         HideError();
    }

    private void SetPath(string? p, bool raise)
    {
        var v       = p?.Trim() ?? "";
        var changed = v != _path;
        _path = v;
        _paths = Core.Models.FileValue.ReadPaths(_path);
        _clickTimer.Stop();
        _pendingPath = null;
        if (raise && changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    // ── Показ ───────────────────────────────────────────────────────────────────

    private void UpdateVisual()
    {
        if (_editing) return;
        Link.IsVisible = true;
        MultiLinks.IsVisible = false;
        MultiLinks.Children.Clear();

        if (_paths.Count > 1 && (!IsAuto || System.IO.Directory.Exists(_autoFolder)))
        {
            ShowMultipleFiles();
            return;
        }

        var path = _paths.Count == 1 ? _paths[0] : "";

        // Авто-режим (read-only): «папка недоступна» / имя-ссылка / «файл не найден».
        if (IsAuto)
        {
            if (!System.IO.Directory.Exists(_autoFolder))
            {
                Link.Text            = "папка недоступна";
                Link.Foreground      = Secondary();
                Link.TextDecorations = null;
                LinkHost.Cursor      = new Cursor(StandardCursorType.Hand);
            }
            else if (path.Length > 0 && File.Exists(path))
            {
                Link.Text            = SafeFileName(path);
                Link.Foreground      = Accent();
                Link.TextDecorations = TextDecorations.Underline;
                LinkHost.Cursor      = new Cursor(StandardCursorType.Hand);
            }
            else
            {
                Link.Text            = path.Length > 0 ? SafeFileName(path) + " — файл не найден" : "файл не найден";
                Link.Foreground      = Secondary();
                Link.TextDecorations = null;
                LinkHost.Cursor      = Cursor.Default;
            }
            return;
        }

        if (path.Length == 0)
        {
            Link.Text            = "Выбрать файл…";     // серое, некликабельное
            Link.Foreground      = Secondary();
            Link.TextDecorations = null;
            LinkHost.Cursor      = Cursor.Default;
            return;
        }

        var name = SafeFileName(path);
        if (File.Exists(path))
        {
            Link.Text            = name;                 // синяя подчеркнутая ссылка
            Link.Foreground      = Accent();
            Link.TextDecorations = TextDecorations.Underline;
            LinkHost.Cursor      = new Cursor(StandardCursorType.Hand);
        }
        else
        {
            Link.Text            = name + " — файл не найден";
            Link.Foreground      = Secondary();
            Link.TextDecorations = null;
            LinkHost.Cursor      = new Cursor(StandardCursorType.Hand);
        }
    }

    // Разделители — отдельные элементы: запятая внутри имени остается частью своей ссылки.
    // Весь список обрезается границами LinkHost, поэтому невидимый хвост не получает нажатий.
    private void ShowMultipleFiles()
    {
        Link.IsVisible = false;
        MultiLinks.IsVisible = true;
        LinkHost.Cursor = Cursor.Default;
        foreach (var path in _paths)
        {
            if (MultiLinks.Children.Count > 0)
                MultiLinks.Children.Add(new TextBlock { Text = ", ", Foreground = Secondary() });

            var exists = File.Exists(path);
            var link = new TextBlock
            {
                Text = SafeFileName(path) + (exists ? "" : " — файл не найден"),
                Foreground = exists ? Accent() : Secondary(),
                TextDecorations = exists ? TextDecorations.Underline : null,
                Cursor = exists ? new Cursor(StandardCursorType.Hand) : Cursor.Default,
            };
            link.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(link).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                QueueClick(path);
            };
            MultiLinks.Children.Add(link);
        }
    }

    // Показать плашку на пару секунд (клик по «папка недоступна»): та же плашка-оверлей.
    private void FlashError(string message)
    {
        ShowError(message);
        _errFlash?.Stop();
        _errFlash = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _errFlash.Tick += (_, _) => { _errFlash?.Stop(); HideError(); };
        _errFlash.Start();
    }

    // Имя для показа — общий источник с фильтром таблицы (валидный путь → базовое имя,
    // произвольное значение от прежнего типа → как есть).
    private static string SafeFileName(string path) => Core.Models.FileValue.DisplayName(path);

    private void ShowError(string message) { NoticeBanner.Show(message); ErrPop.IsOpen = true; }
    private void HideError()               { NoticeBanner.Hide();        ErrPop.IsOpen = false; }

    // Приложение потеряло фокус — прячем плашку (top-level popup), чтобы не висела поверх
    // других программ; текст ошибки в баннере сохраняем и вернем на Activated.
    private void OnWindowDeactivated(object? sender, EventArgs e) => ErrPop.IsOpen = false;

    // Вернулись в приложение — если еще правим и путь неверный, показываем плашку снова.
    private void OnWindowActivated(object? sender, EventArgs e) { if (_editing) LiveValidate(); }

    // Fallback-цвета — на случай вызова до attach; после подключения перекрашиваем из темы.
    // Значения совпадают с палитрой (Colors.axaml), чтобы даже до attach цвет не «уплывал».
    private IBrush Accent()    => Brush("BrushAccent",        Color.Parse("#007AFF"));
    private IBrush Secondary() => Brush("BrushSecondaryText", Colors.Gray);
    private IBrush Primary()   => Brush("BrushPrimaryText",   Colors.Black);
    private IBrush Red()       => Brush("BrushRed",           Color.Parse("#FF3B30"));

    private IBrush Brush(string key, Color fallback) =>
        this.TryFindResource(key, out var r) && r is IBrush b ? b : new SolidColorBrush(fallback);
}
