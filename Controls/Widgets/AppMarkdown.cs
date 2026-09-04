using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using SmartBase.Controls.Markup;
using SmartBase.Utils;

namespace SmartBase.Controls.Widgets;

/// <summary>
/// Показ markdown-текста. ЕДИНСТВЕННЫЙ способ рисовать markdown в приложении: и вид, и разбор
/// здесь одни на все места (инструкция пользователя, описания расширений, описание записи,
/// «Просмотр» заметки) — см. docs/developer/ui/markdown.md.
///
/// Разбор — Markdig, показ — свои контролы (<c>Controls/Markup</c>). Готового движка тут больше
/// нет: Markdown.Avalonia приносил свой набор узлов, свой набор поддержанной разметки и свою
/// тему, и «Просмотр» неизбежно расходился с живым редактором заметок, который всегда считал
/// разметку Markdig. Теперь оба режима разбирают текст ОДНИМ парсером с одним набором
/// расширений, и расхождение может взяться только из разного оформления — а оно сведено в
/// <see cref="MarkupTheme"/>.
///
/// Виджет — вертикальная стопка блоков, а не прокрутка (вопреки прежнему имени базового типа):
/// прокручивает его внешний <c>ScrollViewer</c>, а воздух вокруг текста задается <c>Margin</c>
/// в разметке — так требует стандарт прокрутки ([[scrolling]]).
/// </summary>
public class AppMarkdown : StackPanel
{
    /// <summary>
    /// Набор расширений намеренно узкий: включено только то, что живой редактор заметок умеет
    /// показывать по ходу набора. Расширение, добавленное здесь и не поддержанное там, сразу
    /// разводит «Просмотр» с «Динамическим просмотром» — ровно та беда, ради которой свой рендер
    /// и писался (см. <c>Core/Notes/NoteMarkupScanner</c> — тот же набор).
    /// </summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough | EmphasisExtraOptions.Marked)
        .UseTaskLists()
        .UseAutoLinks()
        .Build();

    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<AppMarkdown, string?>(nameof(Markdown));

    public static readonly StyledProperty<bool> SelectionEnabledProperty =
        AvaloniaProperty.Register<AppMarkdown, bool>(nameof(SelectionEnabled));

    public static readonly StyledProperty<string?> BasePathProperty =
        AvaloniaProperty.Register<AppMarkdown, string?>(nameof(BasePath));

    /// <summary>Исходный текст. Пустая строка — законное значение и означает «очистить».</summary>
    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>Можно ли выделять текст мышью. По умолчанию нет — выделение нужно там, где текст
    /// читают и цитируют (инструкция пользователя), а не везде.</summary>
    public bool SelectionEnabled
    {
        get => GetValue(SelectionEnabledProperty);
        set => SetValue(SelectionEnabledProperty, value);
    }

    /// <summary>Папка, относительно которой ищутся картинки. Пусто — только абсолютные пути.</summary>
    public string? BasePath
    {
        get => GetValue(BasePathProperty);
        set => SetValue(BasePathProperty, value);
    }

    /// <summary>
    /// Нажали на ссылку. Подписчик решает сам и, если справился, помечает событие обработанным
    /// (<see cref="LinkEventArgs.Handled"/>); необработанные адреса виджет открывает системой.
    /// Нужно справочнику: там ссылка ведет на другой документ инструкции, а не наружу.
    /// </summary>
    public event EventHandler<LinkEventArgs>? LinkClicked;

    /// <summary>Размер шрифта, на котором собран текущий вид. Размеры запекаются в прогоны
    /// числами, поэтому смена «Размера шрифта» требует пересборки — как в таблице и в дереве
    /// боковой панели.</summary>
    private int _renderedFontDelta = FontScale.Delta;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty ||
            change.Property == SelectionEnabledProperty ||
            change.Property == BasePathProperty) Render();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        FontScale.Changed += OnFontScaleChanged;

        // Догоняющая пересборка: размер могли поменять, пока виджет был вне дерева.
        if (_renderedFontDelta != FontScale.Delta) Render();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        FontScale.Changed -= OnFontScaleChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnFontScaleChanged() => Render();

    private void Render()
    {
        _renderedFontDelta = FontScale.Delta;

        var text = Markdown;
        if (string.IsNullOrEmpty(text))
        {
            Children.Clear();
            return;
        }

        MarkupBlocks.Fill(this, text, Parse(text), SelectionEnabled, OnLinkActivated, BasePath);
    }

    /// <summary>
    /// Разбор не имеет права уронить экран: текст приходит и от человека, и из описаний
    /// расширений. Сорвавшийся разбор — это показ без разметки, а не окно с ошибкой.
    /// </summary>
    private static Markdig.Syntax.MarkdownDocument? Parse(string text)
    {
        // Полное имя: короткое «Markdown» перехватывается свойством этого же класса.
        try { return Markdig.Markdown.Parse(text, Pipeline); }
        catch { return null; }
    }

    private void OnLinkActivated(object? sender, string url)
    {
        var args = new LinkEventArgs(url);
        LinkClicked?.Invoke(this, args);
        if (args.Handled || string.IsNullOrWhiteSpace(url)) return;

        // Открывает система: open (macOS), ассоциация (Windows), xdg-open (Linux). Молча —
        // отказ открыть ссылку не повод показывать человеку окно с ошибкой.
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex)
        {
            // Нет ассоциации или адрес не тот — переход просто не случится; причина — в журнале.
            Serilog.Log.Warning(ex, "Не удалось открыть ссылку системой: {Url:l}", url);
        }
    }
}

/// <summary>Нажатая ссылка. <see cref="Handled"/> означает «переход уже сделан, дальше не надо».</summary>
public sealed class LinkEventArgs(string url) : EventArgs
{
    public string Url { get; } = url;
    public bool   Handled { get; set; }
}
