using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SmartBase.Integrations;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Окно «Поток событий»: что нам присылает сторонняя программа. Адрес и секрет берутся из
/// выбранного соединения — здесь только путь, способ подписки и правило, как из события
/// получаются строки.
///
/// Все, что касается той стороны, вводит человек: путь, темы, путь к записям, имя колонки-ключа
/// — из ее документации, как путь и тело запроса. Окно не знает ни одной сторонней программы и
/// ничего не подставляет за человека.
///
/// Кнопка «Проверить» открывает поток по-настоящему, слушает его <see cref="ListenSeconds"/>
/// секунд и показывает СЫРЫЕ события: без них поток не настроить — угадывать имена полей и
/// место записей в событии невозможно.
///
/// Возвращает <see cref="IntegrationStream"/> или null при отмене.
/// </summary>
public partial class IntegrationStreamDialog : Window
{
    /// <summary>
    /// Сколько секунд слушает проверка. Столько хватает, чтобы вызвать событие на той стороне
    /// руками и увидеть его здесь; дольше — человек решит, что окно зависло.
    /// </summary>
    public const int ListenSeconds = 20;

    /// <summary>Сколько сообщений показывает проверка: форму события видно по первым же.</summary>
    public const int ShownMessages = 5;

    /// <summary>Сколько знаков одного сообщения показывается — событие бывает и длинным.</summary>
    public const int ShownLength = 4000;

    private readonly int                                  _id;
    private readonly IReadOnlyList<IntegrationConnection> _connections;
    private readonly Func<int, string>                    _secret;

    private CancellationTokenSource? _listening;

    /// <param name="card">Правится существующая карточка либо <c>IntegrationStream.New()</c>.</param>
    /// <param name="connections">Соединения для выбора; пустой список — окно бесполезно, и
    /// вызывающий код до него не доводит.</param>
    /// <param name="secret">Секрет соединения по ключу — только для кнопки «Проверить»;
    /// расшифровывается в момент проверки и здесь не хранится.</param>
    public IntegrationStreamDialog(
        IntegrationStream card,
        IReadOnlyList<IntegrationConnection> connections,
        Func<int, string> secret)
    {
        InitializeComponent();

        _id          = card.Id;
        _connections = connections;
        _secret      = secret;

        Title = card.Id > 0 ? "Поток событий" : "Новый поток событий";

        ConnectionCombo.ItemsSource = connections.Select(c => c.Name).ToList();
        ConnectionCombo.SelectedIndex = Math.Max(0,
            connections.ToList().FindIndex(c => c.Id == card.ConnectionId));

        NameBox.Text      = card.Name;
        PathBox.Text      = card.Path;
        TopicsBox.Text    = string.Join("\n", card.Subscriptions);
        ItemsPathBox.Text = card.ItemsPath;
        KeyFieldBox.Text  = card.KeyField;

        ProtocolCombo.SelectedIndex = card.Protocol == StreamProtocol.Stomp ? 1 : 0;

        ApplyProtocol();
        ShowFullAddress();

        ProtocolCombo.SelectionChanged   += (_, _) => { ApplyProtocol(); Reset(); };
        ConnectionCombo.SelectionChanged += (_, _) => { ShowFullAddress(); Reset(); };
        PathBox.TextChanged              += (_, _) => { ShowFullAddress(); Reset(); };

        foreach (var box in new[] { NameBox, TopicsBox, ItemsPathBox, KeyFieldBox })
            box.TextChanged += (_, _) => Reset();

        TestBtn.Click    += async (_, _) => await TestAsync();
        CancelBtn.Click  += (_, _) => Close(null);
        ConfirmBtn.Click += (_, _) => Confirm();
        KeyDown          += (_, e) => { if (e.Key == Key.Escape) Close(null); };

        // Окно закрыли посреди проверки — соединение закрывается вместе с ним: слушать поток,
        // на который никто не смотрит, незачем.
        Closed += (_, _) => _listening?.Cancel();

        Opened += (_, _) => NameBox.Focus();
    }

    // ── Сбор карточки ─────────────────────────────────────────────────────────

    private IntegrationConnection? Connection()
    {
        var index = ConnectionCombo.SelectedIndex;
        return index >= 0 && index < _connections.Count ? _connections[index] : null;
    }

    private StreamProtocol Protocol() =>
        ((ProtocolCombo.SelectedItem as ComboBoxItem)?.Tag as string) == "stomp"
            ? StreamProtocol.Stomp
            : StreamProtocol.Plain;

    private IntegrationStream Build() =>
        new(_id,
            NameBox.Text?.Trim() ?? "",
            Connection()?.Id ?? 0,
            PathBox.Text?.Trim() ?? "",
            Protocol(),
            Protocol() == StreamProtocol.Stomp
                ? (TopicsBox.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                        .Select(t => t.Trim())
                                        .Where(t => t.Length > 0)
                                        .ToList()
                : [],
            ItemsPathBox.Text?.Trim() ?? "",
            KeyFieldBox.Text?.Trim() ?? "");

    // ── Действия ──────────────────────────────────────────────────────────────

    private void Confirm()
    {
        var card     = Build();
        var problems = card.Problems();

        if (problems.Count > 0)
        {
            ShowError(string.Join("; ", problems));
            return;
        }

        Close(card);
    }

    /// <summary>
    /// Открыть поток и показать, что по нему приходит. Событий за время проверки может не
    /// прийти вовсе — это не ошибка, и сказано об этом словами: поток открыт, а говорить той
    /// стороне было не о чем.
    /// </summary>
    private async Task TestAsync()
    {
        var card       = Build();
        var connection = Connection();
        var problems   = card.Problems();

        if (connection is null) { ShowError("Не выбрано соединение"); return; }
        if (problems.Count > 0) { ShowError(string.Join("; ", problems)); return; }

        HideError();
        HideAnswer();
        ShowStatus($"Открываю поток и слушаю {ListenSeconds} с…", ok: false);
        TestBtn.IsEnabled = false;

        var messages = new List<string>();
        var total    = 0;
        var opened   = false;

        using var listening = new CancellationTokenSource(TimeSpan.FromSeconds(ListenSeconds));
        _listening = listening;

        try
        {
            var host = connection.BaseAddress?.Host ?? "localhost";

            // Соединение слушается в фоне, и его обратные вызовы приходят не с UI-потока:
            // собранное читается только после того, как слушание кончилось.
            var ended = await Task.Run(() => EgressHttp.ListenAsync(
                connection, _secret(connection.Id), card.Path,
                StreamEvents.Greeting(card, host),
                opened: () => opened = true,
                received: text =>
                {
                    total++;
                    if (messages.Count < ShownMessages) messages.Add(text);
                    return true;
                },
                listening.Token));

            if (!IsVisible) return;   // окно закрыли посреди проверки

            ShowStatus(Verdict(ended, opened, total), opened);

            if (messages.Count > 0) ShowAnswer(Raw(messages, total));
        }
        finally
        {
            _listening = null;
            if (IsVisible) TestBtn.IsEnabled = true;
        }
    }

    private static string Verdict(EgressResponse ended, bool opened, int total)
    {
        if (!opened)
            return ended.Reason == EgressDenyReason.None
                ? "Поток не открылся: " + ended.Explanation
                : "Запрещено политикой: " + ended.Explanation;

        var head = total == 0
            ? $"Поток открыт. За {ListenSeconds} с не пришло ничего — той стороне было не о чем "
              + "сообщить. Вызовите событие на ней и проверьте снова."
            : $"Поток открыт. Пришло сообщений: {total}";

        // Остановка по времени — обычный конец проверки, и говорить о ней нечего. Сказать надо,
        // когда поток закрыла та сторона: это уже сведение о ней.
        return ended.Explanation is "остановлено" or "" ? head : head + " · " + ended.Explanation;
    }

    /// <summary>Пришедшее как есть: служебные знаки кадров заменены видимыми.</summary>
    private static string Raw(List<string> messages, int total)
    {
        var text = new StringBuilder();

        foreach (var message in messages)
        {
            var shown = message.Replace("\0", "").Replace("\r\n", "\n").Trim('\n');

            if (shown.Length > ShownLength)
                shown = shown[..ShownLength] + $"\n… показано {ShownLength:N0} знаков из {shown.Length:N0}";

            if (text.Length > 0) text.Append("\n\n────────\n\n");
            text.Append(shown);
        }

        if (total > messages.Count)
            text.Append($"\n\n… показаны первые {messages.Count} из {total}");

        return text.ToString();
    }

    // ── Оформление ────────────────────────────────────────────────────────────

    /// <summary>Темы есть только у подписки: у простого потока подписываться не на что.</summary>
    private void ApplyProtocol()
    {
        var pos = Position;
        TopicsBlock.IsVisible = Protocol() == StreamProtocol.Stomp;
        KeepWindowPosition(pos);
    }

    /// <summary>Итоговый адрес и вердикт политики — не дожидаясь нажатия «Проверить».</summary>
    private void ShowFullAddress()
    {
        var connection = Connection();
        var address    = connection is null ? null : Build().BuildAddress(connection);

        if (address is null)
        {
            FullAddressText.Text = "Итоговый адрес соберется, когда путь будет заполнен правильно.";
            return;
        }

        var decision = EgressGate.Check(address);

        FullAddressText.Text = "Откроется по адресу: " + address
            + (decision.Allowed ? "" : ". Политика: " + decision.Explanation);
    }

    /// <summary>Карточку правят — прежний итог проверки к ней уже не относится.</summary>
    private void Reset()
    {
        HideError();
        HideStatus();
        HideAnswer();
    }

    private void ShowAnswer(string text)
    {
        var pos = Position;
        AnswerBox.Text      = text;
        AnswerBox.IsVisible = true;
        KeepWindowPosition(pos);
    }

    private void HideAnswer()
    {
        if (!AnswerBox.IsVisible) return;
        var pos = Position;
        AnswerBox.IsVisible = false;
        KeepWindowPosition(pos);
    }

    private void ShowStatus(string text, bool ok)
    {
        var pos = Position;
        StatusText.Text       = text;
        StatusText.Foreground = Brush(ok ? "BrushGreen" : "BrushSecondaryText",
                                      ok ? "#34C759"    : "#8E8E93");
        StatusText.IsVisible  = true;
        KeepWindowPosition(pos);
    }

    private void HideStatus()
    {
        if (!StatusText.IsVisible) return;
        var pos = Position;
        StatusText.IsVisible = false;
        KeepWindowPosition(pos);
    }

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

    private static IBrush Brush(string key, string fallbackHex) =>
        Application.Current!.TryFindResource(key, out var res) && res is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallbackHex));

    private void KeepWindowPosition(PixelPoint pos) =>
        SmartBase.Controls.Behaviors.WindowPlacement.KeepPositionAfterLayout(this, pos);
}
