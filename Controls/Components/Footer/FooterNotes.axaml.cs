using Avalonia.Controls;

namespace SmartBase.Controls.Components;

/// <summary>Как показана открытая заметка.</summary>
public enum NotesViewMode
{
    /// <summary>Только исходный текст markdown.</summary>
    Source,
    /// <summary>Слева правка, справа живой просмотр.</summary>
    Split,
    /// <summary>Только собранный документ.</summary>
    Preview,
}

/// <summary>
/// Футер раздела «Заметки»: переключатель режима показа. Высота, отступы и стиль кнопки — как у
/// остальных полос приложения (56 / 16 / navbtn 30×30). Сам контент режима применяет родитель по
/// событию <see cref="ModeChanged"/>.
///
/// Когда заметка не открыта, футер гасится через <c>IsEnabled</c>, а не прячется: он занимает 56 px
/// и несет линию-разделитель — скрытие сдвинуло бы тело раздела.
/// </summary>
public partial class FooterNotes : UserControl
{
    public event EventHandler<NotesViewMode>? ModeChanged;

    private NotesViewMode _mode = NotesViewMode.Split;

    public FooterNotes()
    {
        InitializeComponent();
        ModeBtn.Click += (_, _) => Mode = Next(_mode);
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

    public static NotesViewMode Next(NotesViewMode mode) => mode switch
    {
        NotesViewMode.Source => NotesViewMode.Split,
        NotesViewMode.Split  => NotesViewMode.Preview,
        _                    => NotesViewMode.Source,
    };

    public static string Label(NotesViewMode mode) => mode switch
    {
        NotesViewMode.Source => "Исходный текст",
        NotesViewMode.Split  => "Разделенный вид",
        _                    => "Просмотр",
    };

    /// <summary>Ключ для app.db — совпадает со значениями <c>AppSettings.LoadNotesViewMode</c>.</summary>
    public static string Key(NotesViewMode mode) => mode switch
    {
        NotesViewMode.Source => "source",
        NotesViewMode.Split  => "split",
        _                    => "preview",
    };

    public static NotesViewMode FromKey(string? key) => key switch
    {
        "source"  => NotesViewMode.Source,
        "preview" => NotesViewMode.Preview,
        _         => NotesViewMode.Split,
    };

    private void UpdateIcon()
    {
        // Иконка показывает режим, В КОТОРЫЙ переведет клик; подпись — режим текущий.
        var next = Next(_mode);
        IconToSource.IsVisible  = next == NotesViewMode.Source;
        IconToSplit.IsVisible   = next == NotesViewMode.Split;
        IconToPreview.IsVisible = next == NotesViewMode.Preview;

        ModeLabel.Text = Label(_mode);
        ToolTip.SetTip(ModeBtn, Label(next));
    }
}
