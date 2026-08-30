using Avalonia.Controls;

namespace SmartBase.Controls.Components;

/// <summary>Как показана открытая заметка.</summary>
public enum NotesViewMode
{
    /// <summary>Только исходный текст markdown.</summary>
    Source,
    /// <summary>Разметка применяется прямо в тексте, в одном окне.</summary>
    Live,
    /// <summary>Только собранный документ.</summary>
    Preview,
}

/// <summary>
/// Футер раздела «Заметки»: переключатель режима показа. Высота, отступы и стиль кнопки — как у
/// остальных полос приложения (56 / 16 / navbtn 30×30). Сам контент режима применяет родитель по
/// событию <see cref="ModeChanged"/>.
///
/// Кнопка показывает иконкой текущий режим, а по клику открывает список из трех вариантов с
/// галочкой у выбранного — так же, как переключатель вида в Obsidian. Перебор по кругу не годится:
/// из трех состояний нужный ищется вслепую, а список называет каждый вариант словом.
///
/// Когда заметка не открыта, футер гасится через <c>IsEnabled</c>, а не прячется: он занимает 56 px
/// и несет линию-разделитель — скрытие сдвинуло бы тело раздела.
/// </summary>
public partial class FooterNotes : UserControl
{
    public event EventHandler<NotesViewMode>? ModeChanged;

    private NotesViewMode _mode = NotesViewMode.Live;

    public FooterNotes()
    {
        InitializeComponent();
        ModeBtn.Click += (_, _) => OpenModeMenu();
        UpdateIcon();
    }

    public NotesViewMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            UpdateIcon();
            ModeChanged?.Invoke(this, _mode);
        }
    }

    /// <summary>Поставить режим без события — при восстановлении из настроек.</summary>
    public void SetModeSilent(NotesViewMode mode)
    {
        _mode = mode;
        UpdateIcon();
    }

    /// <summary>Название режима — общее для меню и тултипа. Слова взяты из Obsidian.</summary>
    public static string Label(NotesViewMode mode) => mode switch
    {
        NotesViewMode.Source => "Просмотр исходного кода",
        NotesViewMode.Live   => "Динамический просмотр",
        _                    => "Просмотр",
    };

    /// <summary>Ключ для app.db — совпадает со значениями <c>AppSettings.LoadNotesViewMode</c>.</summary>
    public static string Key(NotesViewMode mode) => mode switch
    {
        NotesViewMode.Source => "source",
        // Ключ исторический: раньше этот режим показывал две панели рядом. Менять его — значит
        // сбросить сохраненный выбор у всех ради строки, которой никто не видит.
        NotesViewMode.Live   => "split",
        _                    => "preview",
    };

    public static NotesViewMode FromKey(string? key) => key switch
    {
        "source"  => NotesViewMode.Source,
        "preview" => NotesViewMode.Preview,
        _         => NotesViewMode.Live,
    };

    // Порядок как в Obsidian: сперва готовый вид, потом исходный код, потом живая разметка.
    private static readonly NotesViewMode[] MenuOrder =
        [NotesViewMode.Preview, NotesViewMode.Source, NotesViewMode.Live];

    // Меню строится заново на каждый клик: галочка стоит у текущего режима, а он между показами
    // меняется (в том числе снаружи — при восстановлении из настроек).
    private void OpenModeMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.TopEdgeAlignedLeft };

        foreach (var mode in MenuOrder)
        {
            var item = new MenuItem { Header = MenuIcons.ChoiceHeader(Label(mode), mode == _mode) };
            var target = mode;
            item.Click += (_, _) => Mode = target;
            menu.Items.Add(item);
        }

        menu.Open(ModeBtn);
    }

    private void UpdateIcon()
    {
        // Иконка и тултип называют режим ТЕКУЩИЙ: выбор происходит в списке, кнопке остается
        // показывать, где человек сейчас.
        IconSource.IsVisible  = _mode == NotesViewMode.Source;
        IconLive.IsVisible   = _mode == NotesViewMode.Live;
        IconPreview.IsVisible = _mode == NotesViewMode.Preview;

        ToolTip.SetTip(ModeBtn, Label(_mode));
    }
}
