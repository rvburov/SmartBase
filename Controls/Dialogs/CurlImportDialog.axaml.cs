using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using SmartBase.Integrations;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Окно «Вставить cURL»: человек копирует строку из документации сторонней программы, программа разбирает ее
/// и отдает две заготовки — соединение и запрос. Это самый короткий путь к «настрой сам»
/// (docs/ROADMAP-API.md, раздел 5).
///
/// Разбор ничего не выполняет и не читает с диска — строку принесли снаружи. Секрет из нее
/// выносится отдельно и в заголовках карточки не остается; что именно программа сочла секретом
/// и чего не разобрала, окно говорит вслух до нажатия «Заполнить» — молчаливый перенос
/// параметра из адреса выглядел бы как потеря параметра.
///
/// Возвращает <see cref="CurlImport"/> или null при отмене.
/// </summary>
public partial class CurlImportDialog : Window
{
    private CurlImport? _parsed;

    public CurlImportDialog()
    {
        InitializeComponent();

        ParseBtn.Click   += (_, _) => Parse();
        CancelBtn.Click  += (_, _) => Close(null);
        ConfirmBtn.Click += (_, _) => Close(_parsed);
        KeyDown          += (_, e) => { if (e.Key == Key.Escape) Close(null); };

        CurlBox.TextChanged += (_, _) =>
        {
            _parsed              = null;
            ConfirmBtn.IsEnabled = false;
            ResultBlock.IsVisible = false;
            HideError();
        };

        Opened += (_, _) => CurlBox.Focus();
    }

    private void Parse()
    {
        HideError();

        if (!CurlParser.TryParse(CurlBox.Text ?? "", out var import, out var error))
        {
            _parsed               = null;
            ConfirmBtn.IsEnabled  = false;
            ResultBlock.IsVisible = false;
            ShowError(error);
            return;
        }

        _parsed              = import;
        ConfirmBtn.IsEnabled = true;

        var pos = Position;

        ResultText.Text = $"{Method(import!.Method)} {import.Address}{import.Path}"
                        + (import.Headers.Count > 0 ? $" · заголовков: {import.Headers.Count}" : "")
                        + (import.Body.Length > 0 ? " · с телом" : "")
                        + " · " + AuthText(import);

        NotesText.IsVisible = import.Notes.Count > 0;
        NotesText.Text      = import.Notes.Count > 0
            ? "Обратите внимание: " + string.Join("; ", import.Notes)
            : "";

        ResultBlock.IsVisible = true;
        KeepWindowPosition(pos);
    }

    private static string Method(RequestMethod method) => method.ToString().ToUpperInvariant();

    private static string AuthText(CurlImport import) => import.Auth switch
    {
        ConnectionAuth.Bearer => "токен сохранен отдельно",
        ConnectionAuth.Basic  => $"имя «{import.AuthName}» и пароль сохранены отдельно",
        ConnectionAuth.Header => $"ключ из заголовка {import.AuthName} сохранен отдельно",
        ConnectionAuth.Query  => $"ключ из параметра {import.AuthName} сохранен отдельно",
        _                     => "авторизации в строке нет",
    };

    private void ShowError(string message)
    {
        var pos = Position;
        NoticeBanner.Show(message);
        KeepWindowPosition(pos);
    }

    private void HideError()
    {
        if (!NoticeBanner.IsVisible) return;
        var pos = Position;
        NoticeBanner.Hide();
        KeepWindowPosition(pos);
    }

    private void KeepWindowPosition(PixelPoint pos) =>
        SmartBase.Controls.Behaviors.WindowPlacement.KeepPositionAfterLayout(this, pos);
}
