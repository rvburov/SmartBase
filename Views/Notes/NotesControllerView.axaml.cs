using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SmartBase.Controls.Behaviors;
using SmartBase.Controls.Components;
using SmartBase.Controls.Dialogs;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Notes;
using SmartBase.Utils;

namespace SmartBase.Views.Notes;

/// <summary>
/// Раздел «Заметки»: шапка с именем файла, тело (исходник / разделенный вид / просмотр) и футер с
/// переключателем режима. Данные берутся не из базы, а из папки на диске — см.
/// <see cref="VaultOperations"/>; сам раздел ничего не знает о рабочем пространстве и коллекциях.
/// </summary>
public partial class NotesControllerView : UserControl
{
    /// <summary>Человек нажал «Открыть настройки» в пустом состоянии.</summary>
    public event EventHandler? OpenSettingsRequested;
    /// <summary>Имя заметки изменено в шапке — дерево должно это увидеть.</summary>
    public event EventHandler<(string RelativePath, string NewName)>? NoteRenamed;

    private string _vault = "";

    // Открытая заметка: относительный путь (ключ для настроек), абсолютный (для записи) и то,
    // что нужно вернуть в файл байт в байт.
    private string? _openRelative;
    private string  _openFull = "";
    private bool    _openBom;
    private string  _openNewLine = "\n";
    private NoteSnapshot _snapshot;
    private long _openLength;

    private bool _dirty;
    // Гасит автосохранение, пока текст в редактор кладем мы сами, а не человек.
    private bool _loading;
    // Конфликт уже показан — не спрашиваем повторно на каждое нажатие клавиши.
    private bool _conflictAsked;

    private ScrollViewer? _editorScroll;

    private readonly DispatcherTimer _saveTimer    = new() { Interval = NoteAutosave.Debounce };
    private readonly DispatcherTimer _previewTimer = new() { Interval = NoteAutosave.PreviewDebounce };

    public NotesControllerView()
    {
        InitializeComponent();

        FooterBar.SetModeSilent(FooterNotes.FromKey(AppSettings.LoadNotesViewMode()));
        FooterBar.ModeChanged += (_, mode) =>
        {
            AppSettings.SaveNotesViewMode(FooterNotes.Key(mode));
            ApplyMode();
        };

        NoteEditor.TemplateApplied += (_, e) =>
        {
            _editorScroll = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
            UpdateScrollBars();
        };

        NoteEditor.TextChanged += (_, _) => OnTextChanged();

        _saveTimer.Tick    += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); RenderPreview(); };

        HeaderBar.TitleEdited += (_, name) =>
        {
            if (_openRelative is null) return;
            // Сохраняем черновик ДО переименования: File.Move уведет файл из-под записи.
            FlushPendingSave();
            NoteRenamed?.Invoke(this, (_openRelative, name));
        };

        OpenSettingsBtn.Click += (_, _) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

        // Ctrl/Cmd+S — явный жест поверх автосохранения. Туннелем: иначе клавишу разберет TextBox.
        AddHandler(KeyDownEvent, OnPanelKeyDown, RoutingStrategies.Tunnel);

        ApplyMode();
        ApplyState();
    }

    // ── Хранилище и открытие ────────────────────────────────────────────────────

    /// <summary>Сменилась папка хранилища. Открытая заметка при этом теряет смысл.</summary>
    public void SetVault(string? path)
    {
        var next = (path ?? "").Trim();
        if (string.Equals(next, _vault, StringComparison.Ordinal)) { ApplyState(); return; }

        FlushPendingSave();
        _vault = next;
        CloseNote();
        ApplyState();
    }

    /// <summary>Пересмотреть состояние — при входе в раздел и после операций в дереве.</summary>
    public void Refresh()
    {
        // Файл могли удалить или переименовать снаружи, пока раздел был закрыт.
        if (_openRelative is not null && !File.Exists(_openFull)) CloseNote();
        ApplyState();
    }

    /// <summary>Открыть заметку по относительному пути.</summary>
    public void OpenNote(string relativePath)
    {
        FlushPendingSave();

        var rel  = VaultRules.Normalize(relativePath);
        var full = VaultScanner.FullPathOf(_vault, rel);

        var (content, error) = VaultOperations.ReadNote(full);
        if (content is null)
        {
            CloseNote();
            ApplyState();
            Toast.Error(HeaderBar, error ?? "Не удалось открыть заметку.");
            return;
        }

        _openRelative  = rel;
        _openFull      = full;
        _openBom       = content.Bom;
        _openNewLine   = content.NewLine;
        _snapshot      = content.Snapshot;
        _openLength    = content.Snapshot.Length;
        _dirty         = false;
        _conflictAsked = false;

        _loading = true;
        NoteEditor.Text = content.Text;
        _loading = false;

        AppSettings.SaveNotesLastFile(rel);
        HeaderBar.SetTitle(NoteStem(rel));
        ApplyState();
        RenderPreview();
    }

    /// <summary>Выбран файл, который раздел не открывает: показываем пояснение вместо содержимого.</summary>
    public void ShowUnsupported(string fileName)
    {
        FlushPendingSave();
        CloseNote();
        UnsupportedHint.Text =
            $"Файл «{fileName}» не открывается в заметках — здесь показываются только файлы Markdown " +
            "(.md и .markdown). Открыть его можно в той программе, которая с ним работает.";
        HeaderBar.SetTitle(fileName);
        HeaderBar.SetEditable(false);
        ApplyState(unsupported: true);
    }

    /// <summary>Открытая заметка переехала (переименование или перенос) — идем за ней.</summary>
    public void NotePathChanged(string oldRelative, string newRelative)
    {
        if (_openRelative is null) return;
        if (!VaultRules.IsInside(_openRelative, oldRelative)) return;

        _openRelative = VaultRules.Rebase(_openRelative, oldRelative, newRelative);
        _openFull     = VaultScanner.FullPathOf(_vault, _openRelative);
        _snapshot     = VaultOperations.Snapshot(_openFull) ?? _snapshot;

        AppSettings.SaveNotesLastFile(_openRelative);
        HeaderBar.SetTitle(NoteStem(_openRelative));
    }

    /// <summary>Открытая заметка удалена (сама или вместе с папкой) — закрываем ее.</summary>
    public void NoteRemoved(string relativePath)
    {
        if (_openRelative is null || !VaultRules.IsInside(_openRelative, relativePath)) return;
        CloseNote();
        ApplyState();
    }

    /// <summary>Имя переименования не подошло — вернуть заголовку прежнее и объяснить.</summary>
    public void RejectRename(string message)
    {
        if (_openRelative is not null) HeaderBar.SetTitle(NoteStem(_openRelative));
        HeaderBar.ShowTitleError(message);
    }

    private void CloseNote()
    {
        _saveTimer.Stop();
        _previewTimer.Stop();

        _openRelative = null;
        _openFull     = "";
        _dirty        = false;

        _loading = true;
        NoteEditor.Text = "";
        _loading = false;

        NotePreview.Markdown = "";
        HeaderBar.SetTitle("");
        HeaderBar.SetEditable(false);
    }

    private static string NoteStem(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        return VaultRules.IsMarkdown(name) ? Path.GetFileNameWithoutExtension(name) : name;
    }

    // ── Состояние тела ──────────────────────────────────────────────────────────

    private bool HasVault       => _vault.Length > 0;
    private bool VaultIsMissing => HasVault && !Directory.Exists(_vault);

    private void ApplyState(bool unsupported = false)
    {
        var noVault = !HasVault || VaultIsMissing;
        var open    = _openRelative is not null;

        VaultHint.IsVisible       = noVault;
        UnsupportedHint.IsVisible = !noVault && unsupported;
        PickHint.IsVisible        = !noVault && !unsupported && !open;

        if (noVault)
        {
            VaultHintLead.Text = HasVault ? "Папка хранилища недоступна. Откройте" : "Откройте";
            VaultHintTail.Text = HasVault
                ? "Настройки → Заметки и выберите ее заново"
                : "Настройки → Заметки и выберите папку хранилища";
            HeaderBar.SetPlaceholder("Заметки");
        }
        else HeaderBar.SetPlaceholder("Выберите заметку");

        // Футер гасим, а не прячем: он занимает 56 px и несет линию — скрытие сдвинуло бы тело.
        FooterBar.IsEnabled = open;
        HeaderBar.SetEditable(open);

        ApplyMode();
    }

    // ── Режимы показа ───────────────────────────────────────────────────────────

    private void ApplyMode()
    {
        var open = _openRelative is not null;
        var mode = FooterBar.Mode;

        // В разделенном виде живое превью строится по каждой паузе в наборе. Для больших файлов
        // это дорого (Markdown.Avalonia собирает дерево синхронно на UI-потоке), поэтому крупная
        // заметка показывается исходником — иначе набор текста начнет спотыкаться.
        if (open && mode == NotesViewMode.Split && _openLength > VaultOperations.MaxLivePreviewBytes)
            mode = NotesViewMode.Source;

        var showEditor  = open && mode is NotesViewMode.Source or NotesViewMode.Split;
        var showPreview = open && mode is NotesViewMode.Preview or NotesViewMode.Split;
        var split       = showEditor && showPreview;

        NoteEditor.IsVisible     = showEditor;
        EditorScrollBar.IsVisible = showEditor;
        SplitLine.IsVisible      = split;
        NotePreviewBox.IsVisible = showPreview;
        PreviewScrollBar.IsVisible = showPreview;

        // Колонка со звездой и невидимым содержимым все равно заняла бы место — схлопываем в 0.
        var star = new GridLength(1, GridUnitType.Star);
        var none = new GridLength(0);
        BodyGrid.ColumnDefinitions[0].Width = showEditor  ? star : none;
        BodyGrid.ColumnDefinitions[3].Width = showPreview ? star : none;

        UpdateScrollBars();
        if (showPreview) RenderPreview();
    }

    // У каждого видимого блока своя полоса: общая на два блока показывала бы положение только
    // одного из них (правило docs/developer/ui/scrolling.md).
    private void UpdateScrollBars()
    {
        ScrollBarLink.SetSource(EditorScrollBar,  _editorScroll);
        ScrollBarLink.SetSource(PreviewScrollBar, NotePreviewBox);
    }

    private void RenderPreview()
    {
        if (!NotePreviewBox.IsVisible) return;
        NotePreview.Markdown = NoteEditor.Text ?? "";
    }

    // ── Правка и сохранение ─────────────────────────────────────────────────────

    private void OnTextChanged()
    {
        if (_loading || _openRelative is null) return;

        _dirty = true;
        // Рестарт на каждый символ: пишем через паузу после набора, а не по каждой букве —
        // иначе каждое нажатие уходило бы на диск, а следом в облачный синхронизатор.
        _saveTimer.Stop();
        _saveTimer.Start();

        if (NotePreviewBox.IsVisible)
        {
            _previewTimer.Stop();
            _previewTimer.Start();
        }
    }

    private void OnPanelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.S) return;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Meta)) return;

        FlushPendingSave();
        e.Handled = true;
    }

    /// <summary>
    /// Дописать черновик немедленно. Синхронный и без диалогов: его зовут при уходе из раздела,
    /// потере фокуса окном и закрытии программы — модалка в этот момент открылась бы поверх
    /// закрывающегося окна. Идемпотентен: повторный вызов без правок ничего не делает.
    /// </summary>
    public void FlushPendingSave()
    {
        _saveTimer.Stop();
        if (_dirty) SaveNow();
    }

    private void SaveNow()
    {
        if (!_dirty || _openRelative is null) return;

        // Файл изменили снаружи — молча перезаписывать чужую правку нельзя. Останавливаем
        // автосохранение и спрашиваем; до ответа текст остается в редакторе целым.
        if (NoteAutosave.ChangedOutside(_snapshot, VaultOperations.Snapshot(_openFull)))
        {
            if (!_conflictAsked)
            {
                _conflictAsked = true;
                _ = ResolveConflict();
            }
            return;
        }

        var result = VaultOperations.WriteNoteAtomic(_openFull, NoteEditor.Text ?? "", _openBom, _openNewLine);
        if (!result.Ok)
        {
            // Автосохранение останавливаем до следующей правки — иначе тост замигает каждую паузу.
            _dirty = false;
            Toast.Error(NoteEditor, result.Error ?? "Не удалось сохранить заметку.");
            return;
        }

        _dirty      = false;
        _snapshot   = VaultOperations.Snapshot(_openFull) ?? _snapshot;
        _openLength = _snapshot.Length;
    }

    // Развязка конфликта: либо перезаписываем своей версией, либо кладем свою рядом отдельным
    // файлом и перечитываем чужую. Ни одна из двух версий не пропадает.
    private async Task ResolveConflict()
    {
        if (VisualRoot is not Window owner || _openRelative is null) return;

        var name = Path.GetFileName(_openFull);
        var overwrite = await new ConfirmDialog(
                $"Файл «{name}» изменила другая программа после того, как вы его открыли.\n" +
                "Перезаписать его вашей версией или сохранить вашу версию отдельным файлом?",
                "Перезаписать", "Сохранить копию")
            .ShowDialog<bool>(owner);

        var folder = Path.GetDirectoryName(_openFull) ?? _vault;

        if (overwrite)
        {
            var result = VaultOperations.WriteNoteAtomic(_openFull, NoteEditor.Text ?? "", _openBom, _openNewLine);
            if (result.Ok)
            {
                _dirty    = false;
                _snapshot = VaultOperations.Snapshot(_openFull) ?? _snapshot;
            }
            else Toast.Error(NoteEditor, result.Error ?? "Не удалось сохранить заметку.");
        }
        else
        {
            var copyName = NoteAutosave.ConflictName(name, DateTime.Now);
            var created  = VaultOperations.CreateNote(folder, copyName);
            if (created.Ok)
            {
                VaultOperations.WriteNoteAtomic(created.Path!, NoteEditor.Text ?? "", _openBom, _openNewLine);
                Toast.Error(NoteEditor, $"Ваша версия сохранена как «{Path.GetFileName(created.Path)}».");
            }
            else Toast.Error(NoteEditor, created.Error ?? "Не удалось сохранить копию.");

            _dirty = false;
            OpenNote(_openRelative);   // возвращаемся к тому, что сейчас на диске
        }

        _conflictAsked = false;
    }
}
