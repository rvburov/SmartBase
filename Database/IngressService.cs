using Serilog;
using SmartBase.Integrations;
using SmartBase.Utils;

namespace SmartBase.Database;

/// <summary>Применяет настройки HTTP-приема и управляет временем жизни слушателя.</summary>
public sealed class IngressService : IDisposable
{
    private readonly Func<IReadOnlyList<DatabaseManager>> _databases;

    private IngressHttp?  _door;
    private IngressMode   _mode = IngressMode.Off;
    private int           _port;

    /// <summary>Состояние поменялось — экран перерисовывает строку «слушаю / не слушаю».</summary>
    public event Action? Changed;

    public IngressService(Func<IReadOnlyList<DatabaseManager>> databases) => _databases = databases;

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

        var prefix = IngressGate.Prefix(mode, port);

        var door = IngressHttp.TryOpen(prefix, Answer, out var trouble);

        if (door is null)
        {
            Trouble = trouble.Length > 0 ? trouble : "порт не открылся";

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

        var prefix = IngressGate.Prefix(_mode, _port);

        _door.Dispose();
        _door = null;

        Log.Information("Прием по сети: порт {Prefix:l} закрыт", prefix);

        Changed?.Invoke();
    }

    // ── Ответ на обращение ────────────────────────────────────────────────────

    /// <summary>Обработать обращение на потоке слушателя.</summary>
    private IngressAnswer Answer(IngressCall call)
    {
        try
        {
            return IngressHandler.Handle(call, _databases(), IngressReplies.Current, DateTimeOffset.Now);
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
