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
/// Редактор свойства типа «файл». Значение — <b>абсолютный путь</b> к файлу на диске
/// (ничего не копируется). Показ: имя файла синей ссылкой (клик — открыть через ОС),
/// «имя — файл не найден», если пути нет, и серое «Выбрать файл…» для пустого значения.
/// Правка — по аналогии с датой: ручной ввод пути в поле + правая иконка-скрепка,
/// открывающая системный выбор файла. Неверный путь при ручном вводе — красный текст и
/// плашка-уведомление (<see cref="NoticeBanner"/>). Хранит/отдаёт путь через <see cref="Storage"/>.
/// </summary>
public partial class FileEditor : UserControl
{
    private string _path = "";        // хранимое значение: полный путь или ""
    private string _autoFolder = "";  // непусто → авто-режим (read-only, значения ведёт правило)
    private bool   _editing;
    private bool   _suppress;
    private Window? _window;
    private readonly DispatcherTimer _clickTimer;
    private DispatcherTimer? _errFlash;

    /// <summary>Поднимается при изменении значения пользователем.</summary>
    public event EventHandler? Changed;

    public FileEditor()
    {
        InitializeComponent();

        // Одиночный клик по имени файла обрабатываем с задержкой, чтобы двойной успел
        // его отменить (двойной — ручная правка пути, одиночный — открыть/поправить).
        _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); OnSingleClick(); };

        Pick.Click += async (_, _) => await PickFileAsync();

        LinkHost.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(LinkHost).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            // Авто-режим: ручной правки нет (двойной клик не редактирует), только одиночное действие.
            if (!IsAuto && e.ClickCount >= 2) { _clickTimer.Stop(); BeginEdit(); return; }
            _clickTimer.Stop();
            _clickTimer.Start();
        };

        Input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)       CommitManual();
            else if (e.Key == Key.Escape) CancelEdit();
        };
        Input.LostFocus   += (_, _) => CommitManual();
        Input.TextChanged += (_, _) => { if (!_suppress) LiveValidate(); };

        // Пересчёт показа после подключения к дереву: в конструкторе/инициализаторе (когда
        // задаётся Storage) ресурсы темы ещё не резолвятся (this.TryFindResource → fallback),
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

    /// <summary>Полный путь к файлу для db_field_values. null/"" — файл не выбран.</summary>
    public string? Storage
    {
        get => _path;
        set { _path = value?.Trim() ?? ""; EndEdit(); UpdateVisual(); }
    }

    /// <summary>Папка авто-привязки. Непусто → ячейка read-only (правило ведёт значения): скрепки
    /// и ручного ввода нет, недоступная папка показывается как «папка недоступна». Пусто — обычный
    /// ручной режим.</summary>
    public string AutoFolder
    {
        set
        {
            _autoFolder = value ?? "";
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
        var v = path?.Trim() ?? "";
        if (v == _path) return;               // значение не изменилось
        _path = v;
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
    /// выравнивался с остальными колонками; на карточке записи — 0 (задаёт карточка).</summary>
    public double ContentLeftPadding
    {
        set
        {
            LinkHost.Padding = new Thickness(value, 0, 0, 0);
            Input.Padding    = new Thickness(value, 0, 0, 0);
        }
    }

    // ── Клики/действия показа ───────────────────────────────────────────────────

    private void OnSingleClick()
    {
        if (IsAuto)
        {
            // Авто-режим read-only: папка недоступна → плашка; файл есть → открыть; иначе — ничего.
            if (!System.IO.Directory.Exists(_autoFolder)) { FlashError("Папка недоступна"); return; }
            if (_path.Length > 0 && File.Exists(_path)) OpenFile();
            return;
        }
        if (_path.Length == 0) return;              // «Выбрать файл…» — не кликабельно
        if (File.Exists(_path)) OpenFile();
        else BeginEdit();                           // файла нет — даём поправить путь руками
    }

    // Открыть файл штатным приложением ОС. UseShellExecute=true делегирует системе:
    // open (macOS), explorer/ассоциация (Windows), xdg-open (Linux).
    private void OpenFile()
    {
        try { Process.Start(new ProcessStartInfo { FileName = _path, UseShellExecute = true }); }
        catch { /* нечем открыть / нет прав — тихо игнорируем */ }
    }

    private async Task PickFileAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Выберите файл",
            AllowMultiple = false,
        });
        if (files.Count == 0) return;
        SetPath(files[0].Path.LocalPath, raise: true);
        EndEdit();
        UpdateVisual();
    }

    // ── Ручная правка пути ──────────────────────────────────────────────────────

    private void BeginEdit()
    {
        _editing = true;
        _suppress = true;
        Input.Text = _path;
        _suppress = false;
        LinkHost.IsVisible = false;
        Input.IsVisible    = true;
        Input.Focus();
        Input.SelectAll();
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
        _editing = false;
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
        if (raise && changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    // ── Показ ───────────────────────────────────────────────────────────────────

    private void UpdateVisual()
    {
        if (_editing) return;

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
            else if (_path.Length > 0 && File.Exists(_path))
            {
                Link.Text            = SafeFileName(_path);
                Link.Foreground      = Accent();
                Link.TextDecorations = TextDecorations.Underline;
                LinkHost.Cursor      = new Cursor(StandardCursorType.Hand);
            }
            else
            {
                Link.Text            = _path.Length > 0 ? SafeFileName(_path) + " — файл не найден" : "файл не найден";
                Link.Foreground      = Secondary();
                Link.TextDecorations = null;
                LinkHost.Cursor      = Cursor.Default;
            }
            return;
        }

        if (_path.Length == 0)
        {
            Link.Text            = "Выбрать файл…";     // серое, некликабельное
            Link.Foreground      = Secondary();
            Link.TextDecorations = null;
            LinkHost.Cursor      = Cursor.Default;
            return;
        }

        var name = SafeFileName(_path);
        if (File.Exists(_path))
        {
            Link.Text            = name;                 // синяя подчёркнутая ссылка
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
    // других программ; текст ошибки в баннере сохраняем и вернём на Activated.
    private void OnWindowDeactivated(object? sender, EventArgs e) => ErrPop.IsOpen = false;

    // Вернулись в приложение — если ещё правим и путь неверный, показываем плашку снова.
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
