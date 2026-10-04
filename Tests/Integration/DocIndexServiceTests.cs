using System.IO;
using System.Text;
using SmartBase.Database.DocIndex;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Фоновая очередь чтения документов (<c>Database/DocIndex/DocIndexService.cs</c>) — одна на
/// приложение: человек нажимает «Прочитать документы», очередь перемалывает их в своем потоке и
/// складывает текст в кеш.
///
/// Проверяется здесь не «разобрала и положила» (это делает первый тест), а поведение на стыках,
/// где раньше терялась работа человека. Остановка НЕ мгновенная: поток выходит, лишь дочитав
/// отмену, — и все три тонких случая про этот промежуток:
/// <list type="bullet">
/// <item>поставленное сразу после «Остановить» обязано дочитаться новым прогоном, а не пролежать
///       в очереди до перезапуска программы;</item>
/// <item>«Остановить» и сразу «Очистить» обязаны оставить кеш ПУСТЫМ: документ, разобранный,
///       пока человек нажимал кнопки, не должен лечь в уже очищенный файл;</item>
/// <item>изменившийся документ обязан разобраться заново — ключ прошлого прогона не должен
///       помешать взять тот же путь снова.</item>
/// </list>
/// Все это молчаливые отказы: ни сообщения, ни следа в журнале — просто не сработавшая кнопка.
/// </summary>
public sealed class DocIndexServiceTests : IDisposable
{
    // Потолок ожидания прогона. Щедрый: на медленной машине сорок файлов по двести килобайт
    // читаются заметно дольше, чем на быстрой, а тест обязан падать по смыслу, а не по таймингу.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly string          _dir;
    private readonly DocTextIndex    _index;
    private readonly DocIndexService _service;

    private readonly ManualResetEventSlim _finished = new(false);
    private int _finishedCount;

    public DocIndexServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb-docqueue-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _index   = new DocTextIndex(Path.Combine(_dir, DocTextIndex.FileName));
        _service = new DocIndexService(_index);
        _service.Finished += (_, _) => { Interlocked.Increment(ref _finishedCount); _finished.Set(); };
    }

    public void Dispose()
    {
        _service.Stop();
        _service.Wait(Patience).GetAwaiter().GetResult();
        try { Directory.Delete(_dir, recursive: true); } catch { /* занят — уберет система */ }
    }

    // Ждет простоя очереди. Именно простоя, а не одного события «кончил»: прогонов в тесте
    // может быть два (остановка и постановка сразу за ней), и нас интересует конец всей работы.
    private async Task Settle() =>
        Assert.True(await _service.Wait(Patience), "очередь не закончила прогон");

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private List<string> Pile(int count, string word)
    {
        var filler = string.Join(' ', Enumerable.Repeat("строка наполнения текста документа", 600));
        var paths  = new List<string>(count);
        for (var i = 0; i < count; i++)
            paths.Add(Write($"pile-{i:D3}.txt", $"{word} {i}\n{filler}"));
        return paths;
    }

    // Документ, разбор которого заведомо не кончится мгновенно: тесты про промежуток остановки
    // должны застать поток ВНУТРИ разбора, а не после него. Иначе они проверяли бы обычный
    // запуск и молчали бы о любой поломке промежутка.
    private string Heavy(string name)
    {
        var line  = string.Join(' ', Enumerable.Repeat("тяжелая строка наполнения документа", 40));
        var text  = new StringBuilder(24 * 1024 * 1024);
        while (text.Length < 24 * 1024 * 1024) text.Append(line).Append('\n');
        return Write(name, text.ToString());
    }

    // Дать потоку войти в разбор. Само ожидание корректности не решает: пока поток не вышел,
    // Running остается true при ЛЮБОМ тайминге, и поставленное обязано дочитаться новым
    // прогоном. Задержка лишь гарантирует, что промежуток в тесте настоящий.
    private static Task InsideExtract() => Task.Delay(150);

    // ── Обычный прогон ───────────────────────────────────────────────────────

    [Fact]
    public async Task The_queue_reads_what_it_was_given()
    {
        var a = Write("a.txt", "Ведомость объемов работ");
        var b = Write("b.txt", "Пояснительная записка проекта");

        Assert.Equal(2, _service.StartPending([a, b]));
        await Settle();

        Assert.Single(_index.Search("ведомость"));
        Assert.Single(_index.Search("пояснительная"));
        Assert.False(_service.Running);

        // Про конец прогона очередь обязана сказать: по этому событию гаснет строка прогресса
        // и перечитывается выдача поиска.
        Assert.True(_finished.Wait(Patience), "очередь не сообщила об окончании прогона");
        Assert.Equal(1, _finishedCount);
    }

    /// <summary>Один путь, названный дважды, разбирается один раз — очередь считает по ключу пути.</summary>
    [Fact]
    public async Task A_path_named_twice_is_queued_once()
    {
        var a = Write("a.txt", "Первый документ");
        var b = Write("b.txt", "Второй документ");

        Assert.Equal(2, _service.StartPending([a, a, b]));
        await Settle();

        Assert.Equal(2, _index.Stats().Indexed);
    }

    /// <summary>
    /// Измененный документ разбирается заново. Ключ пути держится в очереди только пока документ
    /// в ней стоит: останься он там после прогона навсегда — второе «Прочитать» по тому же файлу
    /// молча ничего не поставило бы, а строка прогресса повисла бы на нуле.
    /// </summary>
    [Fact]
    public async Task A_changed_document_is_read_again_by_the_next_run()
    {
        var path = Write("gost.txt", "Первая редакция документа");
        Assert.Equal(1, _service.StartPending([path]));
        await Settle();
        Assert.Single(_index.Search("первая"));

        Write("gost.txt", "Вторая редакция того же документа, заметно длиннее прежней");
        var pending = _index.Pending([path]);
        Assert.Single(pending);

        Assert.Equal(1, _service.StartPending(pending));
        await Settle();

        Assert.Empty(_index.Search("первая"));
        Assert.Single(_index.Search("вторая"));
    }

    // ── Добавление на ходу ───────────────────────────────────────────────────

    /// <summary>
    /// Пути, поставленные при идущем прогоне, дочитываются им же. Так работает «Прочитать
    /// документы» в меню коллекции, нажатое во время чтения: раньше просьба молча пропадала.
    /// </summary>
    [Fact]
    public async Task Paths_queued_while_a_run_goes_are_read_too()
    {
        _service.StartPending(Pile(30, "наполнение"));
        var extra = Write("extra.txt", "Добавленный на ходу документ");

        Assert.Equal(1, _service.StartPending([extra]));
        await Settle();

        Assert.Single(_index.Search("добавленный"));
        Assert.Equal(31, _index.Stats().Indexed);
    }

    // ── Промежуток остановки ─────────────────────────────────────────────────

    /// <summary>
    /// Документы, поставленные сразу после «Остановить», дочитываются новым прогоном. Поток
    /// уходящего прогона в этот момент еще жив и эти пути уже не разберет — раньше просьба
    /// уходила в ветку «прогон уже идет», и пути оставались лежать в очереди до перезапуска
    /// программы: ни чтения, ни сообщения о том, что кнопка не сработала.
    /// </summary>
    [Fact]
    public async Task Documents_queued_right_after_a_stop_are_read_by_a_new_run()
    {
        var extra = Write("after-stop.txt", "Поставлен сразу после остановки");
        _service.StartPending([Heavy("heavy.txt")]);
        await InsideExtract();

        _service.Stop();
        Assert.Equal(1, _service.StartPending([extra]));

        await Settle();

        Assert.Single(_index.Search("остановки"));
        Assert.False(_service.Running);
    }

    /// <summary>
    /// «Остановить» и сразу «Очистить» оставляют кеш пустым, даже если очистку не дождались.
    /// Разбор идет своим ходом, пока человек нажимает кнопки, и документ, разобранный до
    /// отмены, лег бы в кеш уже ПОСЛЕ очистки: невидимый жилец в только что очищенном файле —
    /// он и не перечитается (кеш считает его разобранным), и не найдется (текста в кеше нет).
    /// Поэтому после отмены очередь в кеш не пишет вовсе.
    /// </summary>
    [Fact]
    public async Task A_cancelled_document_is_not_written_into_a_cleared_cache()
    {
        _service.StartPending([Heavy("heavy.txt")]);
        await InsideExtract();

        // Именно так, без ожидания: это вторая линия обороны — на случай, если очистку позовут
        // из места, которое конца прогона не дожидается.
        _service.Stop();
        _index.Clear();

        await Settle();

        Assert.Equal(0, _index.Stats().Indexed);
        Assert.Empty(_index.Search("тяжелая"));
    }

    /// <summary>
    /// <c>StopAsync</c> возвращается, только когда поток вышел, — на этом держится очистка кеша:
    /// она зовется после него и потому знает, что писать в файл больше некому.
    /// </summary>
    [Fact]
    public async Task StopAsync_returns_only_after_the_thread_left()
    {
        _service.StartPending([Heavy("heavy.txt")]);
        await InsideExtract();

        await _service.StopAsync();

        Assert.False(_service.Running);
    }

    /// <summary>Остановка не теряет уже разобранное — читать заново придется только остаток.</summary>
    [Fact]
    public async Task A_stopped_run_keeps_what_it_had_read()
    {
        var first = Write("first.txt", "Единственный документ прогона");
        Assert.Equal(1, _service.StartPending([first]));
        await Settle();

        _service.Stop();

        Assert.Equal(1, _index.Stats().Indexed);
        Assert.Single(_index.Search("единственный"));
        Assert.Empty(_index.Pending([first]));
    }

    // ── Сорвавшийся прогон ───────────────────────────────────────────────────

    // Кеш, который читается, но не принимает запись, — так выглядят кончившееся место на диске и
    // папка без прав на запись. Триггер дает тот же отказ на каждой записи и на любой машине.
    private void RefuseWrites(bool refuse)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_index.Path};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = refuse
            ? "CREATE TRIGGER refuse BEFORE INSERT ON doc_files BEGIN SELECT RAISE(ABORT, 'кеш не принимает запись'); END;"
            : "DROP TRIGGER refuse;";
        cmd.ExecuteNonQuery();
    }

    // Итог прогона — из самого события: простоя мало, о конце очередь говорит уже после него.
    private DocIndexRunEnd _end;
    private readonly ManualResetEventSlim _ended = new(false);

    private void CatchEnd() => _service.Finished += (_, end) => { _end = end; _ended.Set(); };

    private DocIndexRunEnd NextEnd()
    {
        Assert.True(_ended.Wait(Patience), "очередь не сообщила о конце прогона");
        _ended.Reset();
        return _end;
    }

    /// <summary>
    /// Кеш отказал в записи — прогон встает на первом же документе и говорит почему. Раньше отказ
    /// глотался: прогон «читал» все остальное в никуда, а в конце звучало «прочитано». Очередь
    /// после сбоя пуста, ключи путей свободны: когда причину устранили, те же документы читаются
    /// обычным прогоном.
    /// </summary>
    [Fact]
    public void A_cache_that_refuses_writes_stops_the_run_and_says_why()
    {
        var paths = Pile(5, "отказ");
        CatchEnd();
        RefuseWrites(true);

        Assert.Equal(5, _service.StartPending(paths));
        var end = NextEnd();

        Assert.True(end.Failed, "отказ кеша прошел молча");
        Assert.IsType<Microsoft.Data.Sqlite.SqliteException>(end.Error);
        Assert.Equal(0, end.Done);
        Assert.Equal(5, end.Total);
        Assert.False(_service.Running);
        Assert.Equal(0, _index.Stats().Indexed);

        RefuseWrites(false);
        Assert.Equal(5, _service.StartPending(paths));

        Assert.False(NextEnd().Failed);
        Assert.Equal(5, _index.Stats().Indexed);
    }

    /// <summary>
    /// Исключение в потоке очереди — не повод ронять программу: исключение, ушедшее из своего
    /// потока, завершает процесс целиком. Здесь падает подписчик хода (так упал бы любой дефект
    /// вне разбора документа), и прогон кончается сбоем с этой причиной, а не тестовым процессом.
    /// </summary>
    [Fact]
    public void A_failure_inside_the_queue_thread_ends_the_run_instead_of_the_program()
    {
        var a = Write("a.txt", "Первый документ");
        var b = Write("b.txt", "Второй документ");
        CatchEnd();
        _service.Progress += (_, _) => throw new InvalidOperationException("подписчик хода упал");

        _service.StartPending([a, b]);
        var end = NextEnd();

        Assert.Equal("подписчик хода упал", Assert.IsType<InvalidOperationException>(end.Error).Message);
        Assert.False(_service.Running);
    }
}
