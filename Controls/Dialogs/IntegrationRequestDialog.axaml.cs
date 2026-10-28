using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SmartBase.Integrations;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Окно «Запрос»: что дергаем у сторонней программы. Адрес и секрет берутся из выбранного
/// соединения — здесь только метод, путь, заголовки, тело и разбор ответа.
///
/// Путь относительный, и окно это показывает: под строкой пути собран итоговый адрес. Хост
/// берется только из соединения, потому что политика проверяет именно его — позволь тут полный
/// адрес, и карточка показывала бы одно соединение, а запрос уходил бы куда угодно.
///
/// Кнопка «Проверить» отправляет запрос по-настоящему и показывает СЫРОЙ ответ: без него чужой
/// API не настроить никогда — угадывать, где в ответе лежит массив записей, невозможно.
///
/// Возвращает <see cref="IntegrationRequest"/> или null при отмене.
/// </summary>
public partial class IntegrationRequestDialog : Window
{
    private readonly int                                _id;
    private readonly IReadOnlyList<IntegrationConnection> _connections;
    private readonly Func<int, string>                   _secret;

    /// <param name="card">Правится существующая карточка либо <c>IntegrationRequest.New()</c>.</param>
    /// <param name="connections">Соединения для выбора; пустой список — окно бесполезно, и
    /// вызывающий код до него не доводит.</param>
    /// <param name="secret">Секрет соединения по ключу — только для кнопки «Проверить»;
    /// расшифровывается в момент запроса и здесь не хранится.</param>
    public IntegrationRequestDialog(
        IntegrationRequest card,
        IReadOnlyList<IntegrationConnection> connections,
        Func<int, string> secret)
    {
        InitializeComponent();

        _id          = card.Id;
        _connections = connections;
        _secret      = secret;

        Title = card.Id > 0 ? "Запрос" : "Новый запрос";

        ConnectionCombo.ItemsSource = connections.Select(c => c.Name).ToList();
        ConnectionCombo.SelectedIndex = Math.Max(0,
            connections.ToList().FindIndex(c => c.Id == card.ConnectionId));

        NameBox.Text        = card.Name;
        PathBox.Text        = card.Path;
        HeadersBox.Text     = string.Join("\n", card.Headers.Select(h => $"{h.Name}: {h.Value}"));
        BodyBox.Text        = card.Body;
        PagingFieldBox.Text = card.PagingField;
        ItemsPathBox.Text   = card.ItemsPath;

        MethodCombo.SelectedIndex = card.Method switch
        {
            RequestMethod.Post   => 1,
            RequestMethod.Put    => 2,
            RequestMethod.Patch  => 3,
            RequestMethod.Delete => 4,
            _                    => 0,
        };

        PagingCombo.SelectedIndex = card.Paging switch
        {
            RequestPaging.Page   => 1,
            RequestPaging.Offset => 2,
            RequestPaging.Cursor => 3,
            _                    => 0,
        };

        ApplyMethod();
        ApplyPaging();
        ShowFullAddress();

        MethodCombo.SelectionChanged     += (_, _) => { ApplyMethod(); Reset(); };
        PagingCombo.SelectionChanged     += (_, _) => { ApplyPaging(); Reset(); };
        ConnectionCombo.SelectionChanged += (_, _) => { ShowFullAddress(); Reset(); };
        PathBox.TextChanged              += (_, _) => { ShowFullAddress(); Reset(); };

        foreach (var box in new[] { NameBox, HeadersBox, BodyBox, PagingFieldBox, ItemsPathBox })
            box.TextChanged += (_, _) => Reset();

        TestBtn.Click    += async (_, _) => await TestAsync();
        CancelBtn.Click  += (_, _) => Close(null);
        ConfirmBtn.Click += (_, _) => Confirm();
        KeyDown          += (_, e) => { if (e.Key == Key.Escape) Close(null); };

        Opened += (_, _) => NameBox.Focus();
    }

    // ── Сбор карточки ─────────────────────────────────────────────────────────

    private IntegrationConnection? Connection()
    {
        var index = ConnectionCombo.SelectedIndex;
        return index >= 0 && index < _connections.Count ? _connections[index] : null;
    }

    private RequestMethod Method() => ((MethodCombo.SelectedItem as ComboBoxItem)?.Tag as string) switch
    {
        "post"   => RequestMethod.Post,
        "put"    => RequestMethod.Put,
        "patch"  => RequestMethod.Patch,
        "delete" => RequestMethod.Delete,
        _        => RequestMethod.Get,
    };

    private RequestPaging Paging() => ((PagingCombo.SelectedItem as ComboBoxItem)?.Tag as string) switch
    {
        "page"   => RequestPaging.Page,
        "offset" => RequestPaging.Offset,
        "cursor" => RequestPaging.Cursor,
        _        => RequestPaging.None,
    };

    private IntegrationRequest Build() =>
        new(_id,
            NameBox.Text?.Trim() ?? "",
            Connection()?.Id ?? 0,
            Method(),
            PathBox.Text?.Trim() ?? "",
            ParseHeaders(HeadersBox.Text),
            Method() == RequestMethod.Get ? "" : BodyBox.Text ?? "",
            Paging(),
            PagingFieldBox.Text?.Trim() ?? "",
            ItemsPathBox.Text?.Trim() ?? "");

    /// <summary>Заголовки построчно, как в документации и в строке cURL.</summary>
    private static List<IntegrationHeader> ParseHeaders(string? text)
    {
        var headers = new List<IntegrationHeader>();
        if (string.IsNullOrWhiteSpace(text)) return headers;

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var name = line[..colon].Trim();
            if (name.Length > 0) headers.Add(new IntegrationHeader(name, line[(colon + 1)..].Trim()));
        }

        return headers;
    }

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

    private async Task TestAsync()
    {
        var card       = Build();
        var connection = Connection();
        var problems   = card.Problems();

        if (connection is null) { ShowError("Не выбрано соединение"); return; }
        if (problems.Count > 0) { ShowError(string.Join("; ", problems)); return; }

        HideError();
        ShowStatus("Отправляю…", ok: false);
        AnswerBox.IsVisible = false;
        TestBtn.IsEnabled   = false;

        try
        {
            var answer = await EgressHttp.SendAsync(connection, card, _secret(connection.Id));

            ShowStatus(Verdict(answer), answer.Ok);

            if (answer.Sent)
            {
                var pos = Position;
                AnswerBox.Text      = answer.Body;
                AnswerBox.IsVisible = true;
                KeepWindowPosition(pos);
            }
        }
        finally
        {
            TestBtn.IsEnabled = true;
        }
    }

    private static string Verdict(EgressResponse answer)
    {
        if (!answer.Sent)
            return answer.Reason == EgressDenyReason.None
                ? "Не удалось: " + answer.Explanation
                : "Запрещено политикой: " + answer.Explanation;

        var head = $"Ответ {answer.StatusCode} · {answer.Bytes:N0} байт · {answer.Elapsed.TotalSeconds:0.0} с";

        if (answer.Truncated) head += " · ответ обрезан по потолку";
        if (answer.Explanation.Length > 0) head += " · " + answer.Explanation;

        return head;
    }

    // ── Оформление ────────────────────────────────────────────────────────────

    /// <summary>Тело есть не у всех методов: у GET его не бывает, и поле только мешало бы.</summary>
    private void ApplyMethod() => BodyBlock.IsVisible = Method() != RequestMethod.Get;

    private void ApplyPaging()
    {
        var paging = Paging();

        PagingFieldBlock.IsVisible = paging != RequestPaging.None;
        PagingFieldLabel.Text      = paging == RequestPaging.Cursor ? "Поле ответа" : "Имя параметра";
        PagingFieldBox.Watermark   = paging == RequestPaging.Cursor ? "next_cursor" : "page";
    }

    /// <summary>Итоговый адрес — из адреса соединения и относительного пути.</summary>
    private void ShowFullAddress()
    {
        var connection = Connection();
        var address    = connection is null ? null : Build().BuildAddress(connection);

        FullAddressText.Text = address is null
            ? "Итоговый адрес соберется, когда путь будет заполнен правильно."
            : "Уйдет на: " + address;
    }

    /// <summary>Карточку правят — прежний ответ к ней уже не относится.</summary>
    private void Reset()
    {
        HideError();
        HideStatus();

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
