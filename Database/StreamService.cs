using System.Threading.Channels;
using Avalonia.Threading;
using Serilog;
using SmartBase.Extensions;
using SmartBase.Integrations;

namespace SmartBase.Database;

/// <summary>Что сейчас с потоком событий — для экрана «Потоки».</summary>
/// <param name="Connected">Соединение открыто, события принимаются.</param>
/// <param name="Since">С какого момента длится нынешнее состояние.</param>
/// <param name="Trouble">Почему связи нет; пусто — связь есть либо еще устанавливается.</param>
public sealed record StreamState(bool Connected, DateTimeOffset Since, string Trouble);

/// <summary>
/// Служба потоков событий: держит открытыми потоки, события которых заводят включенные сценарии,
/// на каждую запись события запускает сценарий и отвечает той стороне его итогом.
///
/// Разделение то же, что у остальных служб обмена: <see cref="ScenarioRunner"/> знает «что
/// делать», эта служба — «когда» и «с чем». О содержании событий она не знает ничего: путь, темы
/// подписки и путь к записям названы в карточке потока (<see cref="IntegrationStream"/>), колонки
/// события раскладывает по шагам человек, а ответ уходит обычной карточкой запроса.
///
/// <b>Два потока исполнения, одна очередь.</b> Соединение живет в фоне: оно ждет событий часами и
/// переподключается само. Разбор события идет на UI-потоке — реестр сценариев и настройки живут
/// на общем контексте app.db, как и у службы сценариев. Между ними очередь с одним читателем:
/// события разбираются строго по одному, и два события в одну секунду не получат один номер
/// документа из-за того, что их сценарии шли вперемешку.
///
/// <b>Ответ — по итогу всего сценария</b>, и идет он той же цепочкой, что любая отправка
/// (<see cref="RequestLauncher"/>): замок направления, оба журнала, зеркало.
///
/// <b>Пока программа выключена, события не принимаются и ответов нет.</b> Догонять пропущенное
/// служба не берется: у потока нет памяти, а спросить «что я пропустила» можно только запросом,
/// который знает устройство той стороны.
/// </summary>
public sealed class StreamService : IDisposable
{
    /// <summary>Как часто сверять, какие потоки слушать, со списком сценариев.</summary>
    public const int SyncSeconds = 30;

    /// <summary>
    /// Сколько ключей событий помнится для отсечения повторов. Повтор приходит сразу за
    /// оригиналом — при переподключении или повторной доставке, — и помнить события недельной
    /// давности ради него незачем.
    /// </summary>
    public const int MaxKeys = 500;

    /// <summary>Сценарий отработал на событие. UI-поток; экран обновляет журнал и список.</summary>
    public event Action<Scenario, ScenarioOutcome>? Ran;

    /// <summary>Состояние какого-то потока изменилось. UI-поток; экран перестраивает список.</summary>
    public event Action? Changed;

    private readonly IntegrationsRegistry                 _cards;
    private readonly Func<IReadOnlyList<DatabaseManager>> _databases;
    private readonly IReadOnlyList<IExtension>            _extensions;
    private readonly Action<string>                       _notice;

    // Очередь разбора: события и перемены состояния. Читатель один — разбор строго по одному;
    // писатели — фоновые соединения.
    private readonly Channel<Func<Task>> _jobs =
        Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Dictionary<int, Listener> _listeners = [];
    private readonly CancellationTokenSource   _life      = new();

    // О чем уже сказали человеку — по ключу беды. Поток, лежащий всю ночь, иначе оставил бы к
    // утру сотню одинаковых карточек.
    private readonly Dictionary<string, string> _said = [];

    private DispatcherTimer? _timer;
    private Task?            _pump;

    /// <param name="cards">Реестр интеграций — потоки, сценарии, запросы, соединения.</param>
    /// <param name="databases">Открытые базы на момент разбора.</param>
    /// <param name="extensions">Расширения, чьи действия стоят шагами. Параметром — по той же
    /// причине, что у службы сценариев: тест не должен проверять состав реестра.</param>
    /// <param name="notice">Как сказать человеку о беде.</param>
    public StreamService(IntegrationsRegistry                 cards,
                         Func<IReadOnlyList<DatabaseManager>> databases,
                         IReadOnlyList<IExtension>            extensions,
                         Action<string>                       notice)
    {
        _cards      = cards;
        _databases  = databases;
        _extensions = extensions;
        _notice     = notice;
    }

    // ── Жизнь службы ──────────────────────────────────────────────────────────

    /// <summary>Запуск из UI-потока. Идемпотентен: окно возвращается из трея снова и снова.</summary>
    public void Start()
    {
        if (_timer is not null) return;

        Begin();

        // Сценарий включили, выключили, поправили карточку потока — список слушаемого сверяется
        // раз в полминуты и сразу после правки на экране (он зовет Sync сам).
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(SyncSeconds) };
        _timer.Tick += (_, _) => Sync();
        _timer.Start();
    }

    /// <summary>
    /// Запустить разбор и слушание без таймера — то, что делает <see cref="Start"/>, за вычетом
    /// сверки по часам. Отдельно ради тестов: таймер интерфейса без окна не тикает.
    /// </summary>
    public void Begin()
    {
        if (_pump is not null) return;

        _pump = PumpAsync(_life.Token);

        Sync();
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;

        foreach (var listener in _listeners.Values) listener.Stop();
        _listeners.Clear();

        _jobs.Writer.TryComplete();

        try { _life.Cancel(); }
        catch (ObjectDisposedException) { /* уже остановлена */ }
    }

    /// <summary>
    /// Сверить слушаемые потоки со сценариями. Поток слушается, пока есть хоть один ВКЛЮЧЕННЫЙ
    /// сценарий, который заводят его события: слушать поток, на события которого некому
    /// ответить, значит держать открытое соединение ни за чем. Карточку потока или соединения
    /// поправили — слушание перезапускается с новыми.
    /// </summary>
    public void Sync()
    {
        if (_pump is null) return;   // служба не запущена — слушать некому

        var wanted = new Dictionary<int, (IntegrationStream Stream, IntegrationConnection Connection,
                                          string Secret)>();

        foreach (var scenario in _cards.Scenarios())
        {
            if (!scenario.Automatic || !scenario.Trigger.WatchesStream) continue;
            if (wanted.ContainsKey(scenario.Trigger.StreamId))          continue;

            if (_cards.FindStream(scenario.Trigger.StreamId) is not { } stream)  continue;
            if (_cards.Find(stream.ConnectionId) is not { } connection)          continue;

            wanted[stream.Id] = (stream, connection, _cards.Secret(connection.Id));
        }

        var changed = false;

        foreach (var (id, listener) in _listeners.ToList())
        {
            if (wanted.TryGetValue(id, out var now)
                && listener.Same(now.Stream, now.Connection, now.Secret)) continue;

            listener.Stop();
            _listeners.Remove(id);
            changed = true;
        }

        foreach (var (id, (stream, connection, secret)) in wanted)
        {
            if (_listeners.ContainsKey(id)) continue;

            var listener = new Listener(stream, connection, secret, DateTimeOffset.Now);
            _listeners[id] = listener;

            _ = Task.Run(() => ListenAsync(listener, listener.Token));
            changed = true;
        }

        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// Что сейчас с потоком; <c>null</c> — поток не слушается: нет включенного сценария, который
    /// заводят его события.
    /// </summary>
    public StreamState? State(int streamId) =>
        _listeners.TryGetValue(streamId, out var listener)
            ? new StreamState(listener.Connected, listener.Since, listener.Trouble)
            : null;

    // ── Соединение (фон) ──────────────────────────────────────────────────────

    /// <summary>
    /// Слушать поток, пока не остановят. Оборвался — переподключение с нарастающей паузой: та
    /// сторона могла перезапускаться, и стучаться к ней каждую секунду незачем. Соединение,
    /// прожившее больше минуты, сбрасывает паузу — это была живая связь, а не отказ на входе.
    /// </summary>
    private async Task ListenAsync(Listener listener, CancellationToken ct)
    {
        var pause = TimeSpan.FromSeconds(5);
        var host  = listener.Connection.BaseAddress?.Host ?? "localhost";

        while (!ct.IsCancellationRequested)
        {
            var began  = DateTimeOffset.Now;
            var opened = false;

            EgressResponse ended;

            try
            {
                ended = await EgressHttp.ListenAsync(
                    listener.Connection, listener.Secret, listener.Stream.Path,
                    StreamEvents.Greeting(listener.Stream, host),
                    opened: () =>
                    {
                        opened = true;
                        Post(() => Opened(listener));
                    },
                    received: text =>
                    {
                        var bodies = StreamEvents.Bodies(listener.Stream, text, out var trouble);

                        if (trouble.Length > 0)
                            Post(() => Garbled(listener, "та сторона сообщила об ошибке: " + trouble));

                        foreach (var body in bodies) Post(() => EventAsync(listener, body));

                        return true;
                    },
                    ct);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                ended = EgressResponse.Failed(failure.Message, listener.Connection.Address, 1,
                                              TimeSpan.Zero);
            }

            if (ct.IsCancellationRequested) break;

            var wasOpen = opened;
            Post(() => Closed(listener, ended, wasOpen));

            if (opened && DateTimeOffset.Now - began > TimeSpan.FromMinutes(1))
                pause = TimeSpan.FromSeconds(5);

            try
            {
                await Task.Delay(pause, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            pause = TimeSpan.FromSeconds(Math.Min(pause.TotalSeconds * 2, 60));
        }
    }

    /// <summary>Поставить работу в очередь разбора. После остановки службы — молча некуда.</summary>
    private void Post(Func<Task> job) => _jobs.Writer.TryWrite(job);

    private void Post(Action job) => Post(() => { job(); return Task.CompletedTask; });

    /// <summary>
    /// Читатель очереди. Запускается с UI-потока, и продолжения после каждого ожидания
    /// возвращаются туда же — поэтому работа очереди идет там, где живет реестр.
    /// </summary>
    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _jobs.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await job();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception failure)
                {
                    // Беда одного события не должна уносить разбор всех следующих.
                    Log.Warning(failure, "Потоки событий: разбор события сорвался");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Служба остановлена.
        }
    }

    /// <summary>
    /// Дождаться, пока очередь разберет все, что в нее уже положили. Только для тестов: в работе
    /// очередь не кончается никогда.
    /// </summary>
    public async Task SettleAsync(TimeSpan patience)
    {
        var done = new TaskCompletionSource();
        Post(() => { done.TrySetResult(); return Task.CompletedTask; });

        await done.Task.WaitAsync(patience);
    }

    // ── Состояние потока (UI-поток) ───────────────────────────────────────────

    /// <summary>
    /// Поток подключился. Строка журнала — только если до этого связи не было или это первое
    /// подключение: переподключение после мгновенного обрыва не событие.
    /// </summary>
    private void Opened(Listener listener)
    {
        if (listener.Trouble.Length > 0 || !listener.EverOpened)
            Journal(listener, ok: true, "поток подключен");

        listener.EverOpened = true;
        listener.Connected  = true;
        listener.Since      = DateTimeOffset.Now;
        listener.Trouble    = "";

        _said.Remove(Key(listener));

        Changed?.Invoke();
    }

    /// <summary>
    /// Поток кончился. Строка журнала и слово человеку — только о НОВОЙ беде: та сторона,
    /// лежащая всю ночь, иначе оставила бы по строке на каждую попытку переподключения.
    /// </summary>
    private void Closed(Listener listener, EgressResponse ended, bool opened)
    {
        var reason  = ended.Explanation.Length > 0 ? ended.Explanation : "поток закрыт";
        var trouble = opened ? "связь потеряна: " + reason : "нет связи: " + reason;
        var fresh   = trouble != listener.Trouble;

        if (listener.Connected || fresh) listener.Since = DateTimeOffset.Now;

        listener.Connected = false;
        listener.Trouble   = trouble;

        if (fresh)
        {
            Journal(listener, ok: false, trouble, elapsed: ended.Elapsed);

            // Оборвавшаяся связь сама вернется через несколько секунд, и дергать человека
            // карточкой на каждый обрыв незачем. Сказать надо о том, что НЕ проходит само:
            // поток не открывается вовсе.
            if (!opened) Complain(Key(listener), $"Поток «{listener.Stream.Name}»: {trouble}");
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Пришло то, что не разобралось. Пишется один раз на каждую НОВУЮ причину: поток, который
    /// шлет не то, шлет это непрерывно.
    /// </summary>
    private void Garbled(Listener listener, string trouble)
    {
        if (trouble == listener.Garbled) return;

        listener.Garbled = trouble;
        Journal(listener, ok: false, trouble);
    }

    // ── Разбор события (UI-поток) ─────────────────────────────────────────────

    /// <summary>Событие пришло: разложить на записи и на каждую запустить сценарии.</summary>
    private async Task EventAsync(Listener listener, string body)
    {
        var rows = StreamEvents.Rows(body, listener.Stream.ItemsPath, out var trouble);

        if (trouble.Length > 0)
        {
            Garbled(listener, trouble);
            return;
        }

        foreach (var row in rows) await RowAsync(listener, row);
    }

    /// <summary>
    /// Одна запись события: запустить сценарии и ответить той стороне. Единица работы — запись,
    /// а не событие: у каждой записи свой отправитель и свой ответ.
    /// </summary>
    private async Task RowAsync(Listener listener, IReadOnlyDictionary<string, string> row)
    {
        var key = KeyOf(listener.Stream, row);

        if (key.Length > 0 && listener.Seen(key)) return;

        var handled = false;

        foreach (var scenario in _cards.ScenariosOfStream(listener.Stream.Id))
        {
            // Сценарий успели выключить — события он больше не ждет.
            if (!scenario.Automatic) continue;

            var outcome = await RunAsync(scenario, row);

            // Событие не прошло отбор сценария: оно не для него, и следа не оставляет.
            if (outcome.Idle) continue;

            handled = true;
            Ran?.Invoke(scenario, outcome);

            // Отказ, который действие адресовало отправителю, — разговор, а не поломка: человеку
            // за этим компьютером о нем знать незачем, он лежит в журнале. Сказать надо о том,
            // что отправитель исправить не может.
            if (!outcome.Ok && outcome.Reply.Length == 0)
                Complain("scenario:" + scenario.Id,
                         $"Сценарий «{scenario.Name}» не отработал на событие: "
                         + (outcome.Problem.Length > 0 ? outcome.Problem : outcome.Summary()));
            else
                _said.Remove("scenario:" + scenario.Id);

            await ReplyAsync(scenario, row, outcome);
        }

        // Ключ запоминается ПОСЛЕ работы и только у события, которое кому-то подошло. По одному
        // потоку об одном и том же приходят события разного вида с одним ключом, и запомни мы
        // ключ у первого, не подошедшего, — второе, нужное, отсеклось бы как повтор.
        if (handled && key.Length > 0) listener.Remember(key);
    }

    /// <summary>Запустить сценарий на запись события; сорвавшийся — итогом «не запускался».</summary>
    private async Task<ScenarioOutcome> RunAsync(Scenario scenario, IReadOnlyDictionary<string, string> row)
    {
        try
        {
            return await ScenarioRunner.RunAsync(
                scenario, _cards, _databases(), _extensions, DateTimeOffset.Now, _notice,
                _life.Token, new StepTable([.. row.Keys], [row]));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure)
        {
            // Непредвиденная беда одного сценария не должна оставлять ту сторону без ответа.
            return ScenarioOutcome.Blocked(failure.Message, scenario.Steps.Count);
        }
    }

    /// <summary>
    /// Что ответить той стороне по итогу сценария; пусто — отвечать нечем.
    ///
    /// Сказало что-то отправителю действие — его словами: они о том, что отправитель может
    /// исправить сам. Не сказало, а сценарий не отработал — текстом, который на этот случай
    /// назначил человек: причина не в событии, и подробности лежат в журнале у того, кто
    /// настраивал обмен. Отработал молча — отчетом шагов.
    /// </summary>
    public static string Answer(ScenarioOutcome outcome, string failText)
    {
        if (outcome.Reply.Length > 0) return outcome.Reply;
        if (!outcome.Ok)              return failText;

        return string.Join("\n", outcome.Lines);
    }

    /// <summary>
    /// Ответить карточкой запроса из триггера. Не ушло — человеку одно слово о беде: та сторона
    /// ждет ответа, а узнать, что его не будет, иначе неоткуда.
    /// </summary>
    private async Task ReplyAsync(Scenario scenario, IReadOnlyDictionary<string, string> row,
                                  ScenarioOutcome outcome)
    {
        if (!scenario.Trigger.Replies) return;

        var text = Answer(outcome, scenario.Trigger.FailText);
        if (text.Length == 0) return;

        if (_cards.FindRequest(scenario.Trigger.RequestId) is not { } request
            || _cards.Find(request.ConnectionId) is not { } connection)
        {
            Complain("reply:" + scenario.Id,
                     $"Сценарий «{scenario.Name}»: запрос ответа не найден");
            return;
        }

        var now = DateTimeOffset.Now;
        var db  = scenario.Steps.FirstOrDefault(s => s.DbId > 0)?.DbId ?? 0;

        var sent = await RequestLauncher.SendAsync(
            _cards, connection, request, payload: null, db, now, now,
            scenario.Name, ScenarioRunner.ReplyMark, _life.Token, row, text);

        if (sent.Ok)
            _said.Remove("reply:" + scenario.Id);
        else
            Complain("reply:" + scenario.Id,
                     $"Сценарий «{scenario.Name}»: ответ на событие не ушел — {sent.Explanation}");
    }

    /// <summary>Ключ записи — значение колонки-ключа потока; пусто — повторы не отсекаются.</summary>
    private static string KeyOf(IntegrationStream stream, IReadOnlyDictionary<string, string> row)
    {
        var column = stream.KeyField.Trim();

        return column.Length > 0 && row.TryGetValue(column, out var value) ? (value ?? "").Trim() : "";
    }

    // ── Журнал и жалобы ───────────────────────────────────────────────────────

    private static void Journal(Listener listener, bool ok, string explanation,
                                TimeSpan elapsed = default) =>
        RunJournal.Add(RunEntry.New(
            RunChannel.Stream, listener.Stream.Name,
            listener.Stream.BuildAddress(listener.Connection)?.AbsoluteUri ?? listener.Connection.Address,
            ok, explanation, rows: 0, bytes: 0, elapsed, dbId: 0, DateTimeOffset.Now,
            counts: false, direction: 0));

    private static string Key(Listener listener) => "stream:" + listener.Stream.Id;

    /// <summary>Сказать о беде человеку — но только если она новая для этого ключа.</summary>
    private void Complain(string key, string problem)
    {
        if (_said.TryGetValue(key, out var said) && said == problem) return;

        _said[key] = problem;
        Log.Warning("Потоки событий: {Problem:l}", problem);
        _notice(problem);
    }

    // ── Слушатель одного потока ───────────────────────────────────────────────

    /// <summary>Один слушаемый поток: карточки, секрет и память о нем.</summary>
    private sealed class Listener(IntegrationStream stream, IntegrationConnection connection,
                                  string secret, DateTimeOffset since)
    {
        private readonly CancellationTokenSource _stop = new();

        // Ключи недавних событий: набор — чтобы искать, очередь — чтобы забывать старые.
        private readonly HashSet<string> _keys  = new(StringComparer.Ordinal);
        private readonly Queue<string>   _order = new();

        public IntegrationStream     Stream     { get; } = stream;
        public IntegrationConnection Connection { get; } = connection;
        public string                Secret     { get; } = secret;

        public CancellationToken Token => _stop.Token;

        /// <summary>Соединение открыто.</summary>
        public bool Connected { get; set; }

        /// <summary>С какого момента длится нынешнее состояние.</summary>
        public DateTimeOffset Since { get; set; } = since;

        /// <summary>Последняя беда связи; пусто — связь в порядке либо еще устанавливается.</summary>
        public string Trouble { get; set; } = "";

        /// <summary>Последняя беда разбора — чтобы не писать одно и то же на каждое событие.</summary>
        public string Garbled { get; set; } = "";

        /// <summary>Подключался ли поток хоть раз за жизнь слушателя.</summary>
        public bool EverOpened { get; set; }

        public bool Seen(string key) => _keys.Contains(key);

        public void Remember(string key)
        {
            if (!_keys.Add(key)) return;

            _order.Enqueue(key);

            while (_order.Count > MaxKeys) _keys.Remove(_order.Dequeue());
        }

        /// <summary>Те же ли это карточки — или слушание пора перезапустить.</summary>
        public bool Same(IntegrationStream stream, IntegrationConnection connection, string secret) =>
            Stream.Path == stream.Path
            && Stream.Protocol == stream.Protocol
            && Stream.ItemsPath == stream.ItemsPath
            && Stream.KeyField == stream.KeyField
            && Stream.Name == stream.Name
            && Stream.Subscriptions.SequenceEqual(stream.Subscriptions)
            && Connection.Id == connection.Id
            && Connection.Address == connection.Address
            && Connection.Auth == connection.Auth
            && Connection.AuthName == connection.AuthName
            && Connection.TimeoutSeconds == connection.TimeoutSeconds
            && Connection.Headers.Select(h => h.Name + ":" + h.Value)
                         .SequenceEqual(connection.Headers.Select(h => h.Name + ":" + h.Value))
            && Secret == secret;

        public void Stop()
        {
            try { _stop.Cancel(); }
            catch (ObjectDisposedException) { /* уже остановлен */ }
        }
    }
}
