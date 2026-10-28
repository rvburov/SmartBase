using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SmartBase.Integrations;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Итог окна: карточка соединения и что делать с секретом.
/// </summary>
/// <param name="Connection">Карточка; ключ сохраняется у существующего соединения.</param>
/// <param name="Secret"><c>null</c> — секрет не трогать (человек правил адрес, а не токен);
/// пустая строка — убрать; иначе — заменить.</param>
public sealed record ConnectionDialogResult(IntegrationConnection Connection, string? Secret);

/// <summary>
/// Окно «Соединение»: куда и под кем ходим. Настраивается один раз и переиспользуется всеми
/// запросами, поэтому здесь адрес и секрет, а не в каждой карточке запроса.
///
/// Три вещи, ради которых окно устроено именно так:
///
/// <list type="number">
///   <item><b>Строка политики под адресом.</b> Она отвечает «а можно ли туда вообще» сразу,
///   не дожидаясь «Проверить связь». Отказ шлюза объясняет себя словами — иначе его принимают
///   за поломку сети и чинят перезагрузкой сервера.</item>
///   <item><b>Сохранить можно и запрещенный политикой адрес.</b> Проверяется ФОРМА: политику
///   расширят завтра, и терять из-за нее карточку незачем. Отправку все равно не пропустит
///   шлюз.</item>
///   <item><b>Секрет не живет в карточке.</b> Он уходит отдельным полем результата, а на
///   экране виден только точками; хранит его <c>IntegrationsRegistry</c> в защищенном виде.</item>
/// </list>
///
/// Возвращает <see cref="ConnectionDialogResult"/> или null при отмене.
/// </summary>
public partial class IntegrationConnectionDialog : Window
{
    private readonly int    _id;
    private readonly bool   _hadSecret;
    private readonly string _storedSecret;

    /// <param name="card">Правится существующая карточка либо <c>IntegrationConnection.New()</c>.</param>
    /// <param name="hadSecret">Есть ли у соединения сохраненный секрет.</param>
    /// <param name="storedSecret">Сохраненный секрет — только для кнопки «Проверить связь»:
    /// проверять с пустым токеном бессмысленно, а заставлять человека вводить его заново ради
    /// проверки — тем более.</param>
    public IntegrationConnectionDialog(
        IntegrationConnection card, bool hadSecret = false, string storedSecret = "")
    {
        InitializeComponent();

        _id           = card.Id;
        _hadSecret    = hadSecret;
        _storedSecret = storedSecret;

        Title = card.Id > 0 ? "Соединение" : "Новое соединение";

        NameBox.Text     = card.Name;
        AddressBox.Text  = card.Address;
        AuthNameBox.Text = card.AuthName;
        HeadersBox.Text  = string.Join("\n", card.Headers.Select(h => $"{h.Name}: {h.Value}"));
        TimeoutBox.Text  = card.TimeoutSeconds.ToString();
        RetriesBox.Text  = card.Retries.ToString();

        AuthCombo.SelectedIndex = card.Auth switch
        {
            ConnectionAuth.Header => 1,
            ConnectionAuth.Bearer => 2,
            ConnectionAuth.Basic  => 3,
            ConnectionAuth.Query  => 4,
            _                     => 0,
        };

        SecretBox.Watermark      = hadSecret ? "Оставьте пустым, чтобы не менять" : "";
        ClearSecretCheck.IsVisible = hadSecret;

        ApplyAuth();
        ShowPolicy();

        AuthCombo.SelectionChanged += (_, _) => ApplyAuth();
        AddressBox.TextChanged     += (_, _) => { HideError(); HideStatus(); ShowPolicy(); };

        foreach (var box in new[] { NameBox, AuthNameBox, SecretBox, TimeoutBox, RetriesBox })
            box.TextChanged += (_, _) => { HideError(); HideStatus(); };

        TestBtn.Click    += async (_, _) => await TestAsync();
        CancelBtn.Click  += (_, _) => Close(null);
        ConfirmBtn.Click += (_, _) => Confirm();
        KeyDown          += (_, e) => { if (e.Key == Key.Escape) Close(null); };

        Opened += (_, _) => NameBox.Focus();
    }

    // ── Сбор карточки ─────────────────────────────────────────────────────────

    private ConnectionAuth Auth() => ((AuthCombo.SelectedItem as ComboBoxItem)?.Tag as string) switch
    {
        "header" => ConnectionAuth.Header,
        "bearer" => ConnectionAuth.Bearer,
        "basic"  => ConnectionAuth.Basic,
        "query"  => ConnectionAuth.Query,
        _        => ConnectionAuth.None,
    };

    /// <summary>
    /// Карточка из полей. Числа читаются мягко: пустое поле — значение по умолчанию, а не
    /// ошибка «введите число»; выход за границы поймает <c>Problems()</c> и объяснит человеку.
    /// </summary>
    private IntegrationConnection Build() =>
        new(_id,
            NameBox.Text?.Trim() ?? "",
            AddressBox.Text?.Trim() ?? "",
            Auth(),
            AuthNameBox.Text?.Trim() ?? "",
            ParseHeaders(HeadersBox.Text),
            Number(TimeoutBox.Text, IntegrationConnection.DefaultTimeoutSeconds),
            Number(RetriesBox.Text, IntegrationConnection.DefaultRetries));

    private static int Number(string? text, int fallback) =>
        int.TryParse((text ?? "").Trim(), out var value) ? value : fallback;

    /// <summary>
    /// Заголовки построчно. Строка без двоеточия пропускается молча: человек мог оставить
    /// пометку себе, и ронять из-за нее сохранение соединения не за что.
    /// </summary>
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

    /// <summary>Секрет для сохранения: <c>null</c> — не трогать, "" — убрать.</summary>
    private string? Secret()
    {
        if (ClearSecretCheck.IsChecked == true) return "";

        var typed = SecretBox.Text ?? "";
        if (typed.Length > 0) return typed;

        // Способ авторизации сменили на «без нее» — прежний секрет незачем хранить дальше.
        if (_hadSecret && Auth() == ConnectionAuth.None) return "";

        return _hadSecret ? null : "";
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

        Close(new ConnectionDialogResult(card, Secret()));
    }

    /// <summary>
    /// «Проверить связь»: GET по адресу соединения через тот же шлюз и тот же отправляющий
    /// слой, которым потом пойдут запросы. Проверка кнопкой, которая ходит другой дорогой,
    /// не стоит ничего.
    /// </summary>
    private async Task TestAsync()
    {
        var card     = Build();
        var problems = card.Problems();

        if (problems.Count > 0)
        {
            ShowError(string.Join("; ", problems));
            return;
        }

        HideError();
        ShowStatus("Проверяю…", ok: false);
        TestBtn.IsEnabled = false;

        try
        {
            var secret = (SecretBox.Text ?? "").Length > 0 ? SecretBox.Text! : _storedSecret;
            var answer = await EgressHttp.ProbeAsync(card, secret);

            ShowStatus(Verdict(answer), answer.Ok);
        }
        finally
        {
            TestBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// Итог проверки словами. Код ответа показывается всегда: 401 и 404 значат, что связь
    /// есть, а не сломана, и человек должен видеть разницу.
    /// </summary>
    private static string Verdict(EgressResponse answer)
    {
        if (!answer.Sent)
            return answer.Reason == EgressDenyReason.None
                ? "Не удалось: " + answer.Explanation
                : "Запрещено политикой: " + answer.Explanation;

        var head = $"Ответ {answer.StatusCode}";

        return answer.StatusCode switch
        {
            401 or 403 => head + " — связь есть, но авторизация не принята",
            404        => head + " — связь есть, но такого адреса на сервере нет",
            _ when answer.Ok => head + " — связь есть",
            _          => head + (answer.Explanation.Length > 0 ? " — " + answer.Explanation : ""),
        };
    }

    // ── Оформление ────────────────────────────────────────────────────────────

    /// <summary>Поля авторизации зависят от ее способа: лишние поля путают.</summary>
    private void ApplyAuth()
    {
        var auth = Auth();
        var card = IntegrationConnection.New() with { Auth = auth };

        AuthNameBlock.IsVisible = card.NeedsAuthName;
        SecretBlock.IsVisible   = card.NeedsSecret;

        AuthNameLabel.Text = auth switch
        {
            ConnectionAuth.Basic => "Имя пользователя",
            ConnectionAuth.Query => "Имя параметра",
            _                    => "Имя заголовка",
        };

        AuthNameBox.Watermark = auth switch
        {
            ConnectionAuth.Basic => "ivanov",
            ConnectionAuth.Query => "api_key",
            _                    => "X-Api-Key",
        };
    }

    /// <summary>
    /// Что скажет шлюз об этом адресе. Пустое поле — молчим: подсказывать «адрес не заполнен»
    /// человеку, который его еще не начал вводить, незачем.
    /// </summary>
    private void ShowPolicy()
    {
        var address = (AddressBox.Text ?? "").Trim();

        if (address.Length == 0)
        {
            PolicyText.IsVisible = false;
            return;
        }

        var decision = Uri.TryCreate(address, UriKind.Absolute, out var uri)
            ? EgressGate.Check(uri)
            : EgressDecision.Deny(EgressDenyReason.BadAddress, "адрес не разобрался");

        PolicyText.IsVisible  = true;
        PolicyText.Text       = decision.Allowed
            ? "Политика: адрес разрешен"
            : "Политика: " + decision.Explanation + ". Сохранить карточку можно — отправку шлюз не пропустит.";
        PolicyText.Foreground = Brush(decision.Allowed ? "BrushGreen" : "BrushSecondaryText",
                                      decision.Allowed ? "#34C759"    : "#8E8E93");
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

    // Держим позицию окна при пересчете высоты (SizeToContent), чтобы оно не «прыгало».
    private void KeepWindowPosition(PixelPoint pos) =>
        SmartBase.Controls.Behaviors.WindowPlacement.KeepPositionAfterLayout(this, pos);
}
