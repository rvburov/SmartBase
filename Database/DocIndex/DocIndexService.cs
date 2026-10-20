using System.Threading;
using SmartBase.Core.Documents;

namespace SmartBase.Database.DocIndex;

/// <summary>Ход чтения: сколько разобрано из скольких, какой файл идет и сколько еще ждать.</summary>
/// <param name="Done">Разобрано в текущем прогоне.</param>
/// <param name="Total">Сколько было в очереди на момент старта прогона.</param>
/// <param name="File">Имя текущего документа — его показывает подсказка.</param>
/// <param name="Left">
/// Сколько осталось по средней скорости ЭТОГО прогона без разогрева (<see cref="DocReadPace"/>);
/// <c>null</c>, пока мерить не на чем.
/// Это замер по настоящим файлам человека, а не оценка: документы у всех разные, и угадать
/// скорость заранее нельзя.
/// </param>
public readonly record struct DocIndexProgress(int Done, int Total, string File, TimeSpan? Left);

/// <summary>Чем кончился прогон чтения.</summary>
/// <param name="Done">
/// Сколько документов разобрано и легло в кеш (с любым исходом разбора — и «не удалось прочитать»
/// тоже). Документ, на записи которого прогон сорвался, сюда не входит.
/// </param>
/// <param name="Total">Сколько было в очереди.</param>
/// <param name="Error">
/// Почему прогон сорвался; <c>null</c> — кончился сам или остановлен человеком. Битый документ
/// сюда не попадает: он ложится в кеш как «не удалось прочитать», а прогон идет дальше.
/// </param>
public readonly record struct DocIndexRunEnd(int Done, int Total, Exception? Error)
{
    public bool Failed => Error is not null;
}

/// <summary>
/// Фоновая очередь разбора документов — одна на приложение.
///
/// Раздел отдает ей пути, которые сейчас нужны (документы подключенных коллекций); служба
/// отбрасывает уже разобранные по времени правки и размеру (<see cref="DocTextIndex.Pending"/>) и
/// перемалывает остаток в фоне. Прогресс уходит событием — его показывает тонкая строка в навбаре
/// раздела с крестиком остановки.
///
/// <b>Чтение начинает человек.</b> Очередь не запускается сама ни при заходе в раздел, ни по
/// настройке: разбор двадцати тысяч документов — это часы работы диска и процессора, и решать,
/// когда их потратить, должен тот, чей это компьютер. Зовут ее кнопка «Прочитать документы» в
/// подвале блока поиска и пункт в меню коллекции.
///
/// <b>Один СВОЙ поток, а не задачи из пула.</b> Раньше здесь было два <c>Task.Run</c>, то есть
/// два потока ИЗ ПУЛА, — из того же пула раздел берет потоки на подкачку страниц ленты и на
/// миниатюры. Разбор PDF занимает поток целиком на сотни миллисекунд, поэтому прокрутка
/// конкурировала с ним за одни и те же потоки и дергалась. Собственный поток пул не трогает, а
/// пониженный приоритет отдает ему процессор в последнюю очередь.
///
/// Поток ровно один: спешить больше некуда — запуск теперь осознанный, и фоновая работа не
/// должна отъедать половину машины.
///
/// <b>Поток фоновый (<c>IsBackground</c>).</b> Без этого приложение не закрылось бы, пока
/// очередь не дочитает все документы: обычный поток держит процесс живым.
///
/// <b>Прогон не перезапускается сам.</b> Пришли новые пути, пока очередь идет, — они просто
/// добавляются; повторный <see cref="Start"/> на идущем прогоне ничего не ломает и ничего не
/// начинает заново.
///
/// <b>Остановка — не мгновенная.</b> <see cref="Stop"/> отменяет токен и чистит очередь, но
/// поток выходит сам, дочитав отмену: он может стоять внутри разбора тяжелого файла. Поэтому
/// «остановлен» и «поток еще жив» — два разных состояния, и между ними служба ведет себя
/// определенно: разобранное после отмены в кеш НЕ пишется (иначе документ лег бы туда уже
/// после очистки), а поставленное в очередь в этот промежуток начинает новый прогон, как только
/// прежний поток вышел. Кто должен дождаться конца — зовет <see cref="StopAsync"/>.
///
/// <b>Прогон умеет сорваться, и говорит об этом.</b> Битый документ прогон не останавливает — он
/// ложится в кеш как «не удалось прочитать». Срывается сама работа: кеш не принял запись (нет места
/// на диске, файл испорчен, нет прав на папку) или в потоке упало что-то непредвиденное. Тогда
/// очередь пустеет, а <see cref="Finished"/> несет причину — по ней краснеет значок фоновых работ.
/// Раньше ошибка записи глоталась: прогон «читал» оставшиеся тысячи документов в никуда и в конце
/// сообщал «прочитано», а любое другое исключение в потоке роняло всю программу.
/// </summary>
public sealed class DocIndexService : IAsyncDisposable
{
    private readonly DocTextIndex _index;
    private readonly object       _lock = new();

    private CancellationTokenSource?  _cts;
    private Thread?                   _thread;
    private TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;
    private readonly Queue<string>   _queue = new();
    private readonly HashSet<string> _queued = new(StringComparer.Ordinal);
    private int _done;
    private int _total;
    private long _startedTicks;
    private DocReadPace _pace = new();

    // Уборка сирот сделана за этот запуск программы. Обход всех папок кеша стоит дорого, а
    // сироты появляются не быстрее, чем человек переименовывает документы.
    private int _swept;

    public DocIndexService(DocTextIndex index)
    {
        _index = index;
        _idle.SetResult();
    }

    /// <summary>Кеш, в который пишет очередь — им же пользуется поиск.</summary>
    public DocTextIndex Index => _index;

    /// <summary>Идет ли разбор прямо сейчас.</summary>
    public bool Running { get; private set; }

    /// <summary>
    /// Сколько секунд уходило на документ в последнем прогоне, без разогрева (см.
    /// <see cref="DocReadPace"/>); 0 — прогонов еще не было или прогон был слишком коротким для
    /// замера. Из этого числа берется время в окне подтверждения: оно измерено на НАСТОЯЩИХ файлах
    /// человека, а не угадано.
    /// </summary>
    public double LastSecondsPerDocument { get; private set; }

    /// <summary>Порог размера файла в МБ; 0 — без порога. Ставится из настроек раздела.</summary>
    public int MaxFileMb { get; set; } = DocTextExtractor.DefaultMaxFileMb;

    /// <summary>Ход разбора. Приходит из фонового потока — подписчик сам уходит в UI-поток.</summary>
    public event EventHandler<DocIndexProgress>? Progress;

    /// <summary>
    /// Прогон закончился — сам, по остановке или сорвавшись (<see cref="DocIndexRunEnd.Error"/>):
    /// строка прогресса гаснет. Приходит из фонового потока.
    /// </summary>
    public event EventHandler<DocIndexRunEnd>? Finished;

    /// <summary>
    /// Ставит в очередь то из <paramref name="paths"/>, что еще не разобрано, и запускает прогон,
    /// если он не идет. Возвращает, сколько документов добавилось.
    ///
    /// Отбор «что разбирать» — синхронный и по диску (время правки, размер), поэтому вызывать
    /// это из UI-потока на большом наборе нельзя: зовите <see cref="StartAsync"/>.
    /// </summary>
    public int Start(IReadOnlyCollection<string> paths)
    {
        var pending = _index.Pending(paths, MaxFileMb);
        return StartPending(pending);
    }

    /// <summary>
    /// Поставить в очередь уже отобранное. Отбор дорогой (обход кеша по всем путям), и зовущий
    /// часто делает его сам — чтобы сперва показать человеку, сколько документов предстоит
    /// прочитать, и спросить подтверждение.
    /// </summary>
    public int StartPending(IReadOnlyCollection<string> pending)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (pending.Count == 0) return 0;
            var added = 0;
            foreach (var p in pending)
                if (_queued.Add(DocTextIndex.KeyOf(p))) { _queue.Enqueue(p); added++; }
            if (added == 0) return 0;

            // Прогон идет — очередь добавление на ходу поддерживает. Если он к этому времени
            // остановлен и его поток еще выходит, пути просто лежат в очереди: новый прогон
            // начнет сам уходящий поток, увидев ее непустой (см. конец Drain). Иначе
            // поставленное между «Остановить» и «Прочитать» пролежало бы до перезапуска
            // программы, а кнопка выглядела бы неисправной.
            if (Running) { _total += added; return added; }

            StartRunLocked();
            return added;
        }
    }

    // Новый прогон: счетчики с нуля, свой токен, свой поток. Зовется под замком — из
    // StartPending и из конца прежнего прогона.
    private void StartRunLocked()
    {
        Running       = true;
        _done         = 0;
        _total        = _queue.Count;
        _startedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        _pace         = new DocReadPace();
        _cts          = new CancellationTokenSource();
        if (_idle.Task.IsCompleted)
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var ct = _cts.Token;
        _thread = new Thread(() => Drain(ct))
        {
            // Фоновый — иначе приложение не закроется, пока очередь не дочитает все.
            IsBackground = true,
            // Ниже обычного — чтобы разбор PDF уступал процессор интерфейсу.
            Priority     = ThreadPriority.BelowNormal,
            Name         = "doc-index",
        };
        _thread.Start();
    }

    /// <summary>Отбор и запуск целиком в фоне — отбор ходит на диск, на UI-потоке ему не место.</summary>
    public Task<int> StartAsync(IReadOnlyCollection<string> paths) => Task.Run(() => Start(paths));

    /// <summary>
    /// Останавливает прогон. Разобранное остается в кеше — начинать заново не придется.
    ///
    /// Возвращает управление СРАЗУ: поток выходит сам, когда дочитает отмену. Кому важно, что
    /// очередь в кеш больше не пишет (очистке кеша — важно), тот зовет <see cref="StopAsync"/>.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _queue.Clear();
            _queued.Clear();
        }
    }

    /// <summary>
    /// Остановить и дождаться, пока поток выйдет. Ждет в фоне: вызов идет из обработчика
    /// кнопки, а поток может стоять внутри разбора тяжелого файла.
    /// </summary>
    public Task StopAsync()
    {
        Stop();
        return Wait();
    }

    /// <summary>
    /// Убрать из кеша сирот — один раз за запуск программы и в фоне.
    ///
    /// Зовется разделом, когда перепись закончилась удачей: к этой минуте диск уже ответил про
    /// папки библиотеки, а значит уборке есть на что опереться. При старте программы этого
    /// сказать нельзя — общая папка сервера поднимается позже него.
    ///
    /// Пока идет чтение, уборка ждет следующего раза: оба ходят на один и тот же диск, и
    /// отбирать у чтения его скорость ради уборки незачем.
    /// </summary>
    public Task SweepOnceAsync()
    {
        if (_swept != 0 || Running) return Task.CompletedTask;
        if (Interlocked.Exchange(ref _swept, 1) != 0) return Task.CompletedTask;

        return Task.Run(() =>
        {
            try
            {
                var gone = _index.Sweep();
                if (gone == 0) return;

                Serilog.Log.Information("Кеш документов: убрано сирот — {Count}", gone);
                _index.Maintain();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Кеш документов: уборка не выполнена");
            }
        });
    }

    /// <summary>Дожидается конца прогона — для очистки кеша, тестов и закрытия приложения.</summary>
    public Task Wait()
    {
        lock (_lock) return _idle.Task;
    }

    /// <summary>То же с потолком ожидания; <c>false</c> — не дождались.</summary>
    public async Task<bool> Wait(TimeSpan timeout)
    {
        try { await Wait().WaitAsync(timeout).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
    }

    /// <summary>Запретить новые прогоны, отменить текущий и дождаться выхода без блокировки UI.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            _disposed = true;
            Stop();
            return new ValueTask(_idle.Task);
        }
    }

    private void Drain(CancellationToken ct)
    {
        Exception? error = null;
        try { Worker(ct); }
        catch (OperationCanceledException)
        {
            // Остановка — штатный исход, а не сбой: разобранное уже в кеше.
        }
        catch (Exception ex)
        {
            // Сорвался сам прогон, а не один документ (битый файл разборщик записывает как «не
            // удалось прочитать»): кеш не принял запись или упало непредвиденное. Дальше читать
            // незачем — каждый следующий документ ушел бы в никуда, — а уйди исключение из
            // потока наружу, оно уронило бы всю программу.
            error = ex;
            Serilog.Log.Error(ex, "Чтение документов сорвалось: разобрано {Done} из {Total}", _done, _total);
        }
        finally
        {
            // Обслуживание FTS по следам прогона: каждый документ — это DELETE и INSERT в
            // индексе, а прогон дает их десятки тысяч. Идет тем же фоновым потоком и только
            // после НОРМАЛЬНОГО конца: после «Остановить» человек ждет — очистка кеша стоит
            // за этим ожиданием, и слияние сегментов ее задержало бы. После сбоя — тоже нет:
            // кеш, только что отказавший в записи, слияния не примет.
            if (error is null && _done > 0 && !ct.IsCancellationRequested)
                try { _index.Maintain(); }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "Кеш документов: обслуживание индекса не выполнено");
                }

            bool again;
            int done, total;
            lock (_lock)
            {
                // Замер по тому, что успели: остановленный прогон тоже мерит честно — сколько
                // документов прошло после разогрева, за столько времени. Прогон короче разогрева
                // и пяти документов за ним скорость не меряет — запоминать раздел будет нечего.
                done  = _done;
                total = _total;
                LastSecondsPerDocument = _pace.SecondsPerDocument ?? 0;

                _cts?.Dispose();
                _cts    = null;
                _thread = null;

                // Сорвавшийся прогон уносит и очередь: поставленное на ходу легло бы в тот же
                // кеш, который только что отказал. Человек прочитает остаток, устранив причину, —
                // кнопкой, как и все чтение.
                if (error is not null)
                {
                    _queue.Clear();
                    _queued.Clear();
                }

                // Пока этот прогон умирал, в очередь могли поставить новое — нажать «Прочитать»
                // сразу после «Остановить» человеку никто не мешает. Тогда прогон продолжается
                // новым потоком, и строка прогресса не гаснет: работа-то идет.
                again = _queue.Count > 0;
                if (again)
                {
                    StartRunLocked();
                }
                else
                {
                    Running = false;
                    // Под тем же замком, что и Reset нового прогона: иначе между «прогон
                    // кончился» и этой строкой успел бы начаться следующий, и ожидающий
                    // очистку кеша отпустило бы при живом потоке, который еще пишет в кеш.
                    _idle.TrySetResult();
                }
            }

            // Работа продолжается новым потоком — ни ожидание, ни строка прогресса не гаснут.
            if (!again) RaiseFinished(new DocIndexRunEnd(done, total, error));
        }
    }

    // Конец прогона — подписчикам. Зовется из фонового потока: исключение подписчика ушло бы из
    // потока наружу и уронило бы программу, поэтому оно только пишется в журнал.
    private void RaiseFinished(DocIndexRunEnd end)
    {
        try { Finished?.Invoke(this, end); }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Чтение документов: обработчик конца прогона упал");
        }
    }

    private void Worker(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string path;
            int total;
            lock (_lock)
            {
                if (_queue.Count == 0) return;
                path  = _queue.Dequeue();
                total = _total;
            }

            DocText text;
            // Документ, который читался дольше фонового порога, — строка в журнале диагностики с
            // именем, размером и итогом: тяжелый файл (книга на сотни тысяч строк, том чертежей)
            // виден по журналу, а не угадывается по диспетчеру задач.
            using var slow = SmartBase.Logging.Diag.Measure("чтение документа библиотеки",
                                                            SmartBase.Logging.Diag.Background, FileNameOf(path));
            try
            {
                text = DocTextExtractor.Extract(path, MaxFileMb, ct);
                slow.Context += $", {SizeOf(path)}, {text.Status}" + (text.Pages > 0 ? $", страниц {text.Pages}" : "");
            }
            catch (OperationCanceledException) { slow.Discard(); return; }
            catch
            {
                // Разборщик не должен ронять очередь: один битый файл — это одна строка
                // «не прочитан», а не остановка индексации всей папки.
                text = DocText.Of(DocTextStatus.Failed);
            }

            // После отмены в кеш не пишем. Разбор шел своим ходом, пока человек нажимал
            // «Остановить» и вслед за этим «Очистить»: без проверки этот документ лег бы в
            // кеш уже ПОСЛЕ очистки — один невидимый жилец в только что очищенном файле.
            if (ct.IsCancellationRequested) return;

            // Сбой записи не глотается: это сбой не документа, а самого кеша — нет места на
            // диске, файл испорчен, нет прав на папку. Проглоченный, он превращал прогон в чтение
            // в никуда: оставшиеся тысячи документов разбирались, не ложась в кеш, а в конце
            // человек слышал «прочитано». Исключение срывает прогон (см. Drain). Короткую
            // занятость файла SQLite выжидает сам: busy_timeout, а на Windows еще и повторы
            // ввода-вывода на случай антивируса, — до нас доходит то, что само не пройдет.
            _index.Put(path, text);

            // Ключ держался до этой минуты, чтобы повторное «Прочитать» не поставило в очередь
            // то, что разбирается прямо сейчас. Теперь путь свободен: файл могут изменить, и
            // следующий прогон обязан взять его снова.
            lock (_lock) _queued.Remove(DocTextIndex.KeyOf(path));

            var done = Interlocked.Increment(ref _done);
            _pace.Done(done, System.Diagnostics.Stopwatch.GetElapsedTime(_startedTicks));
            Progress?.Invoke(this, new DocIndexProgress(done, total, FileNameOf(path), _pace.Left(total)));
        }
    }

    private static string SizeOf(string path)
    {
        try { return SmartBase.Utils.Sizes.Text(new System.IO.FileInfo(path).Length); }
        catch { return "размер неизвестен"; }
    }

    private static string FileNameOf(string path)
    {
        var cut = path.LastIndexOfAny(['/', '\\']);
        return cut >= 0 && cut < path.Length - 1 ? path[(cut + 1)..] : path;
    }

    // ── Общий экземпляр ───────────────────────────────────────────────────────
    //
    // Очередь одна на приложение — как RunJournal и SecurityJournal: кеш общий на все базы,
    // и два прогона по одному файлу были бы лишней работой. Ставится при старте (App.axaml.cs);
    // не поставлен — раздел работает без поиска по содержимому, и говорит об этом.

    private static DocIndexService? _current;

    /// <summary>Общая очередь; null — кеш не открылся при старте.</summary>
    public static DocIndexService? Current => _current;

    public static void Attach(DocIndexService service)
    {
        var previous = Interlocked.Exchange(ref _current, service);
        if (previous is not null && !ReferenceEquals(previous, service)) _ = previous.DisposeAsync();
    }

    /// <summary>Отпустить общую очередь при закрытии приложения.</summary>
    public static ValueTask DetachAsync()
    {
        var previous = Interlocked.Exchange(ref _current, null);
        return previous?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    /// <summary>
    /// Открыть кеш по пути и стать общей очередью; <c>false</c> — не удалось, раздел останется
    /// без поиска по содержимому.
    ///
    /// Зовется при старте и повторно — кнопкой «Очистить» на экране настроек: кеш мог не
    /// открыться из-за занятого файла или прав на папку, и такое лечится без перезапуска
    /// программы. Поврежденный файл пересоздает сам <see cref="DocTextIndex"/>.
    /// </summary>
    public static bool Attach(string path, int maxFileMb)
    {
        try
        {
            Attach(new DocIndexService(new DocTextIndex(path)) { MaxFileMb = maxFileMb });
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Кеш документов не открылся: {Path:l}", path);
            return false;
        }
    }
}
