using System.Text;
using Avalonia.Controls;
using SmartBase.Integrations;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Сухой прогон — «Показать, что уйдет» (docs/ROADMAP-API.md, 10.9).
///
/// Запрос собирается целиком тем же кодом, каким его собирает настоящая отправка
/// (<see cref="RequestRunner.Preview"/>), и не отправляется. Это ответ на вопрос, который иначе
/// остается без ответа до первого разбирательства: что именно уходит наружу. Человек, который
/// настроил обмен, видит его глазами; проверяющий — тоже, и до того, как что-то ушло.
///
/// Секреты заменены на <c>***</c> тем же слоем, который маскирует их для журнала: два способа
/// прятать секрет разошлись бы, и разошлись бы в сторону показа.
/// </summary>
public partial class RequestPreviewDialog : Window
{
    private readonly RunPreview _preview;

    public RequestPreviewDialog() : this(RunPreview.Blocked("нечего показывать", false)) { }

    public RequestPreviewDialog(RunPreview preview)
    {
        InitializeComponent();

        _preview = preview;

        if (!preview.Ok)
            NoticeBanner.Show("Запуск не состоится: " + preview.Explanation);
        else if (preview.Outward)
            NoticeBanner.Show("Этот запрос уносит данные наружу. Проверьте тело: уйдет ровно то, "
                            + "что показано ниже.");

        AddressText.Text = preview.SafeAddress.Length > 0
            ? preview.Method.ToString().ToUpperInvariant() + " " + preview.SafeAddress
            : "— адрес не собрался";

        var mono = this.FindResource("FontFamilyMono") as Avalonia.Media.FontFamily;

        foreach (var header in preview.Headers)
        {
            var line = new SelectableTextBlock { Text = header.Name + ": " + header.Value };
            if (mono is not null) line.FontFamily = mono;

            HeadersPanel.Children.Add(line);
        }

        if (preview.Headers.Count == 0)
            HeadersPanel.Children.Add(new TextBlock
            {
                Classes = { "desc" },
                Text    = "Своих заголовков у запроса нет.",
            });

        // Тело есть не у всякого запроса, и пустая рамка под заголовком «Тело» читается как
        // «тело пустое», хотя означает «тела не бывает».
        var hasBody = preview.Body.Trim().Length > 0;

        BodyLabel.IsVisible = hasBody;
        BodyBox.IsVisible   = hasBody;
        BodyText.Text       = preview.Body;

        CopyBtn.Click  += async (_, _) => await CopyAsync();
        CloseBtn.Click += (_, _) => Close();
    }

    /// <summary>
    /// Скопировать показанное целиком. Именно показанное, а не собранное заново: в буфер должно
    /// попасть то же, что человек видел глазами, — иначе в переписке с той стороной обсуждают
    /// разные запросы.
    /// </summary>
    private async Task CopyAsync()
    {
        var text = new StringBuilder();

        text.AppendLine(_preview.Method.ToString().ToUpperInvariant() + " " + _preview.SafeAddress);

        foreach (var header in _preview.Headers)
            text.AppendLine(header.Name + ": " + header.Value);

        if (_preview.Body.Trim().Length > 0)
        {
            text.AppendLine();
            text.AppendLine(_preview.Body);
        }

        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text.ToString());

        NoticeBanner.Show("Скопировано в буфер обмена.");
    }
}
