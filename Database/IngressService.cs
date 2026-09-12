using Serilog;
using SmartBase.Integrations;
using SmartBase.Utils;

namespace SmartBase.Database;

/// <summary>
/// Служба приема по сети: открывает порт, когда его разрешили, и закрывает, когда передумали.
///
/// Сама она ничего не решает — ни кого пускать (это <see cref="IngressHandler"/>), ни можно ли
/// вообще слушать (это <see cref="IngressGate"/>). Ее дело — держать состояние в согласии с
/// настройкой и сказать человеку, почему порт не открылся.
///
/// <b>«Не записалось — не слушаем».</b> То же правило, что и у исходящей двери, и по той же
/// причине: открытый порт на машине с секретными данными — первое, о чем спросит проверяющий.
/// След в журнале безопасности пишется ДО открытия порта; журнал не открылся или строка не
/// легла — порт не открывается вовсе.
///
/// <b>Каждое обращение обрабатывается на своем подключении к app.db.</b> Общий контекст
/// принадлежит интерфейсу, а обращения приходят на потоки слушателя: второй запрос к общему
/// контексту кончается «A second operation was started on this context instance».
/// </summary>
public sealed class IngressService : IDisposable
{
    private readonly Func<IReadOnlyList<DatabaseManager>> _databases;
    private readonly string                               _systemDbPath;

    private IngressHttp?  _door;
    private IngressMode   _mode = IngressMode.Off;
    private int           _port;

    /// <summary>Состояние поменялось — экран перерисовывает строку «слушаю / не слушаю».</summary>
    public event Action? Changed;

    public IngressService(Func<IReadOnlyList<DatabaseManager>> databases, string systemDbPath)
    {
        _databases    = databases;
        _systemDbPath = systemDbPath;
    }

    /// <summary>Порт открыт прямо сейчас.</summary>
    public bool Listening => _door is not null;

    /// <summary>Адрес, который называют отправителю; пусто — не слушаем.</summary>
    public string Address => Listening ? IngressGate.Address(_mode, _port) : "";

    /// <summary>
    /// Почему не слушаем; пусто — либо слушаем, либо прием выключен самим человеком. Текст
    /// нужен экрану: «выключено» и «не смогли открыть» выглядят одинаково, а означают разное.
    /// </summary>
    public string Trouble { get; private set; } = "";

    /// <summary>
    /// Привести состояние в согласие с настройкой: включили — открыть, выключили — закрыть,
    /// сменили порт — переоткрыть. Зовется на старте и при каждой правке настройки; лишний
    /// вызов безвреден.
    /// </summary>
    public void Apply()
    {
        var mode = AppSettings.LoadIngressMode();
        var port = AppSettings.LoadIngressPort();

        var decision = IngressGate.Check(mode, port);

        if (!decision.Allowed)
        {
            // Выключено человеком — это не беда и в строке состояния не жалоба.
            Close(decision.Reason == IngressDenyReason.Disabled ? "" : decision.Explanation);
            return;
        }

        if (Listening && mode == _mode && port == _port) return;   // уже так и слушаем

        Close("");

        // След ДО открытия порта. Не лег — порта не будет: слушатель, о котором не осталось
        // записи, ровно то, чего требование службы безопасности не допускает.
        var prefix = $"http://{IngressGate.LoopbackHost}:{port}/";

        if (!Note(true, prefix, "открыт порт приема данных"))
        {
            Trouble = "журнал безопасности не принял запись, и порт не открыт";
            Log.Warning("Прием по сети: {Reason:l}", Trouble);
            Changed?.Invoke();
            return;
        }

        var door = IngressHttp.TryOpen(prefix, Answer, out var trouble);

        if (door is null)
        {
            Trouble = trouble.Length > 0 ? trouble : "порт не открылся";
            Note(false, prefix, "порт приема не открылся: " + Trouble);

            Log.Warning("Прием по сети: порт {Prefix:l} не открылся — {Reason:l}", prefix, Trouble);
            Changed?.Invoke();
            return;
        }

        _door   = door;
        _mode   = mode;
        _port   = port;
        Trouble = "";

        Log.Information("Прием по сети: слушаю {Prefix:l}", prefix);
        Changed?.Invoke();
    }

    public void Dispose() => Close("");

    private void Close(string trouble)
    {
        Trouble = trouble;

        if (_door is null)
        {
            Changed?.Invoke();
            return;
        }

        var prefix = $"http://{IngressGate.LoopbackHost}:{_port}/";

        _door.Dispose();
        _door = null;

        Note(true, prefix, "порт приема закрыт");
        Log.Information("Прием по сети: порт {Prefix:l} закрыт", prefix);

        Changed?.Invoke();
    }

    /// <summary>След об открытии и закрытии порта. <c>false</c> — строка не легла.</summary>
    private static bool Note(bool allowed, string prefix, string explanation) =>
        SecurityJournal.Add(SecurityEntry.New(
            SecurityEvent.Listen, allowed, CurrentUser.DisplayName, scenario: "",
            address: prefix, fields: "", rows: 0, bytes: 0, bodyHash: "",
            explanation: explanation, dbId: 0, at: DateTimeOffset.Now));

    // ── Ответ на обращение ────────────────────────────────────────────────────

    /// <summary>
    /// Обработать обращение. Живет на потоке слушателя, поэтому реестр открывается СВОЙ — на
    /// время одного обращения. Подключение на обращение здесь не расточительство: обращений
    /// единицы в минуту, а общий контекст принадлежит интерфейсу.
    /// </summary>
    private IngressAnswer Answer(IngressCall call)
    {
        try
        {
            using var system = new SystemDbContext(_systemDbPath);

            return IngressHandler.Handle(call, new IntegrationsRegistry(system),
                                         _databases(), IngressReplies.Current, DateTimeOffset.Now);
        }
        catch (Exception failure)
        {
            // Отвечать оборванным соединением нельзя: отправитель истолкует это как «программы
            // нет» и будет повторять, а причина останется только у нас.
            Log.Warning(failure, "Прием по сети: обращение не обработалось");

            return IngressAnswer.Problem(500,
                "На стороне приемника случилась ошибка; подробности записаны в его журнале.");
        }
    }
}
