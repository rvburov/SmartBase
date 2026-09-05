using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Окно «Данные подключения» серверной базы PostgreSQL: сервер/порт/база/пользователь/SSL
/// для передачи коллеге (пароль намеренно не показывается — его сообщают отдельно).
/// Открывается кнопкой «Данные подключения» в строке PG-базы в настройках и автоматически
/// после создания базы через PgCreateDatabaseDialog. «Скопировать» кладет все строки в
/// буфер обмена одним блоком. Возвращает null.
/// </summary>
public partial class PgDetailsDialog : Window
{
    private readonly string _copyText;

    public PgDetailsDialog(string host, int port, string database, string user, string sslMode)
    {
        InitializeComponent();

        HostText.Text     = host;
        PortText.Text     = port.ToString();
        DatabaseText.Text = database;
        UserText.Text     = user;
        SslText.Text      = SslLabel(sslMode);

        _copyText = $"Сервер: {host}\nПорт: {port}\nБаза данных: {database}\n" +
                    $"Пользователь: {user}\nШифрование (SSL): {SslLabel(sslMode)}";

        CopyBtn.Click  += async (_, _) => await CopyAsync();
        CloseBtn.Click += (_, _) => Close(null);
        KeyDown        += (_, e) => { if (e.Key == Key.Escape) Close(null); };
    }

    // Те же русские подписи, что в комбобоксе окон подключения/создания.
    private static string SslLabel(string sslMode) => sslMode switch
    {
        "Disable" => "Отключено",
        "Require" => "Обязательно",
        _         => "Если доступно",
    };

    private async Task CopyAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(_copyText);
        ShowStatus("Скопировано");
    }

    private void ShowStatus(string text)
    {
        var pos = Position;
        StatusText.Text       = text;
        StatusText.Foreground = Brush("BrushGreen", "#34C759");
        StatusText.IsVisible  = true;
        KeepWindowPosition(pos);
    }

    private static IBrush Brush(string key, string fallbackHex) =>
        Application.Current!.TryFindResource(key, out var res) && res is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallbackHex));

    // Держим позицию окна при пересчете высоты (SizeToContent), чтобы оно не «прыгало».
    private void KeepWindowPosition(PixelPoint pos)
    {
        void Restore(object? _, EventArgs __)
        {
            LayoutUpdated -= Restore;
            if (Position != pos) Position = pos;
        }
        LayoutUpdated += Restore;
    }
}
