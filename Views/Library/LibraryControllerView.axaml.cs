using System.Collections;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SmartBase.Core.Documents;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.DocIndex;
using SmartBase.Database.Repositories;
using SmartBase.Settings;
using SmartBase.Utils;

namespace SmartBase.Views.Library;

/// <summary>
/// Оркестратор раздела «Библиотека документов» — зеркало <c>TasksControllerView</c>: собирает
/// документы всех допущенных баз по отмеченным в сайдбаре коллекциям, держит фильтр типов и
/// поиск, отдает ленту <see cref="DocListView"/>.
///
/// ViewModel у раздела нет намеренно — так же, как у календаря и задач: показ целиком в
/// code-behind, а вся проверяемая логика вынесена в чистые помощники (<see cref="DocRowMap"/>,
/// <see cref="DocLoadPlan"/>, <see cref="DocSelection"/>).
///
/// Два режима чтения, как у задач:
/// <list type="bullet">
/// <item>раздел меньше <see cref="DocLoadCap"/> — весь набор читается в память;</item>
/// <item>больше — лента листается страницами (<see cref="DocVirtualList"/>).</item>
/// </list>
/// Поиск — всегда в памяти и с ограниченной выдачей: текст документов лежит в отдельном кеше,
/// и соединить его с базой постранично нельзя (см. <c>ConstructorRepository.DocsPaging.cs</c>).
/// </summary>
public partial class LibraryControllerView : UserControl
{
    /// <summary>
    /// Документ просят прочитать в правой панели. Сама панель живет в главном окне — там же,
    /// где страница записи конструктора: правая колонка одна на все разделы.
    /// </summary>
    public event EventHandler<DocRecord>? ReadRequested;

    /// <summary>Нажали toggle панели чтения в навбаре: true — открыть, false — закрыть.</summary>
    public event EventHandler<bool>? ReaderToggled;

    /// <summary>Запрос поиска сменился — открытая панель чтения подсвечивает новое слово.</summary>
    public event EventHandler<string>? SearchQueryChanged;

    /// <summary>Сколько документов на базу читаем в память; больше — страницами.</summary>
    private const int DocLoadCap = 10_000;

    /// <summary>Потолок выдачи поиска: и по кешу текста, и по базе.</summary>
    private const int SearchLimit = 500;

    private readonly List<(int DbId, string Name, ConstructorRepository Repo)> _dbs = [];

    private Dictionary<int, HashSet<int>>? _activeByDb;
    private IReadOnlySet<string>? _types;
    private string _searchQuery = "";

    private int _gen;
    private DocVirtualList? _virtual;

    // Отдельный ограничитель для миниатюр: рендер PDF упирается в процессор, и пускать его на
    // все ядра значило бы подвесить прокрутку ради картинок, которые и так второстепенны.
    private readonly SemaphoreSlim _thumbGate = new(2);
    private readonly HashSet<string> _thumbAsked = new(StringComparer.Ordinal);

    // Перепись документов по состоянию текста и то, что сказать про каждый на карточке, — итог
    // ОДНОГО обхода кеша и диска (DocTextIndex.Survey), поэтому числа подвала и чипы не
    // расходятся. Считается в фоне ПОСЛЕ показа ленты: обход спрашивает у диска время правки
    // каждого файла, и держать на нем открытие раздела нельзя. Null — перепись еще не готова.
    private DocCensus _census;
    private IReadOnlyDictionary<string, DocFileState>? _states;
    private readonly DocRunGate _censusGate = new();

    // Гистограммы типов: база → коллекция → сколько документов какого типа. Из них без запроса
    // получается размер ленты в любом виде фильтра типов и числа выпадашки навбара, а фильтр
    // ставится только через выпадашку — к смене типов гистограмма уже посчитана. Раньше каждая
    // смена типов пересчитывала все коллекции заново: на 500 000 документов — секунда.
    //
    // Живут, пока данные не менялись: перечитывание раздела «с прошлого чтения в базах что-то
    // коммитили» (SetActiveIds с force), смена набора баз и Refresh их сбрасывают. Поколение
    // нужно на случай, если сброс пришелся на идущий счет: иначе тот положил бы в кеш
    // гистограмму данных, которых уже нет.
    private readonly Dictionary<int, Dictionary<int, ConstructorRepository.DocTypeHistogram>> _histograms = [];
    private int _histogramsGen;

    // Итоги последнего поиска — из них собирается строка подвала.
    private int  _foundInText;
    private int  _foundByTitle;
    private bool _foundCapped;
    private bool _textFailed;

    // Ход идущего чтения — приходит событиями очереди, живет здесь ради строки подвала.
    private int       _readDone;
    private int       _readTotal;
    private TimeSpan? _readLeft;

    // Лента собирается дольше порога. Раньше на это был оверлей во весь блок; теперь — строка
    // подвала, а лента остается на экране (см. DocLoadPlan).
    private bool _loading;

    public LibraryControllerView()
    {
        InitializeComponent();

        LibrarySettings.Changed += OnLibrarySettingsChanged;
        FontScale.Changed       += OnFontScaleChanged;
    }

    /// <summary>Момент последнего чтения данных (<see cref="ChangeBus.Now"/>): главное окно
    /// сверяет его с моментом последнего коммита в базы и решает, перечитывать ли раздел.</summary>
    public long DataStamp { get; private set; }

    public void Initialize(IReadOnlyList<DatabaseManager> databases)
    {
        SetDatabaseList(databases);

        NavbarControl.LoadTypes       = LoadTypeCountsAsync;
        NavbarControl.LoadTypesFailed = ex =>
            Serilog.Log.Warning(ex, "Библиотека: типы документов не прочитаны");

        NavbarControl.TypesChanged += (_, types) => { _types = types; RefreshCurrent(); };
        NavbarControl.ReaderToggled += (_, open) => ReaderToggled?.Invoke(this, open);

        DocList.DocActivated     += (_, d) => OpenDocument(d);
        DocList.DocReadRequested += (_, d) => ReadRequested?.Invoke(this, d);
        // Запрос поиска и панель чтения связаны: панель подсвечивает то, что искали в разделе.
        // Поэтому новый запрос доезжает до открытой панели — иначе она осталась бы с прошлым
        // словом, и «нашлось 12» в ленте не сходилось бы с подсветкой в документе.
        DocList.SearchRequested += (_, q) =>
        {
            _searchQuery = q;
            RefreshCurrent();
            RaiseQuery();
            _ = ReadOnSearchAsync();
        };
        DocList.SearchCleared   += (_, _) => { _searchQuery = ""; RefreshCurrent(); RaiseQuery(); };

        Follow(DocIndexService.Current);

        RefreshCurrent();
    }

    // Очередь чтения, на чьи события подписан раздел. Очередь бывает не одна за сеанс: кеш, не
    // открывшийся при старте, «Очистить» в настройках открывает заново — это уже новая очередь.
    // Подписка переезжает за ней, иначе ее чтение не сказало бы о себе ничего — ни хода, ни конца,
    // ни сбоя, — а значок фоновых работ так и висел бы на «0 из N» до выхода из программы.
    private DocIndexService? _service;

    private void Follow(DocIndexService? service)
    {
        if (ReferenceEquals(service, _service)) return;
        if (_service is { } old)
        {
            old.Progress -= OnIndexProgress;
            old.Finished -= OnIndexFinished;
        }

        _service = service;
        if (service is null) return;
        service.Progress += OnIndexProgress;
        service.Finished += OnIndexFinished;
    }

    /// <param name="force">
    /// Перечитать, даже если набор галочек тот же: с прошлого чтения в базах что-то поменялось
    /// (правка в конструкторе, запись расширения, чужая правка). Перечитывается тихо: лента уже
    /// на экране, и волна «Загрузка…» на ней выглядела бы как открытие раздела заново.
    /// </param>
    public void SetActiveIds(Dictionary<int, HashSet<int>> activeByDb, bool force = false)
    {
        if (!force && DocLoadPlan.SameActiveIds(_activeByDb, activeByDb)) return;

        // force — в базах что-то коммитили: посчитанные типы могли устареть. Простое касание
        // галочки данных не меняет, и гистограммы остальных коллекций остаются в силе.
        if (force) ForgetHistograms();
        var quiet = force && DocLoadPlan.SameActiveIds(_activeByDb, activeByDb);
        _activeByDb = activeByDb;
        RefreshCurrent(quiet);
    }

    /// <summary>
    /// Чужая правка в базах (сосед по общей базе, поллер журнала изменений) — перечитать ленту
    /// ЛЕГКО. Перечитывать ее на каждую чужую правку вместе с переписью нельзя: перепись
    /// спрашивает у диска каждый файл, а на общей папке сервера это секунды. Поэтому легкий путь
    /// заново читает только базу, а диск спрашивает о новых путях — тех, которых не было ни на
    /// экране, ни в прошлой переписи; страничная лента и так читает лишь видимые страницы. Числа
    /// подвала при этом пересчитываются из карты состояний (<see cref="DocCensus.Of"/>), так что
    /// чипы с ними не расходятся. И не чаще раза в <see cref="RemoteGap"/>: правки, пришедшие за
    /// время ожидания, заберет одно отложенное перечитывание.
    ///
    /// Раньше чужую правку открытая библиотека не получала вовсе: документ, который сосед
    /// прикрепил к записи, появлялся только после перезахода в раздел.
    /// </summary>
    public void RefreshRemote(Dictionary<int, HashSet<int>> activeByDb)
    {
        // Сосед подключил коллекцию к библиотеке или отключил: это уже другая лента, а не правка
        // данных в прежней, — обычное чтение.
        if (!DocLoadPlan.SameActiveIds(_activeByDb, activeByDb)) { SetActiveIds(activeByDb); return; }
        if (_remotePending) return;   // перечитывание уже назначено — оно заберет и эту правку

        var wait = DocLoadPlan.RemoteWait(_remoteLast, DateTime.UtcNow, RemoteGap);
        if (wait <= TimeSpan.Zero) { RefreshRemoteNow(); return; }

        _remotePending = true;
        DispatcherTimer.RunOnce(() => { _remotePending = false; RefreshRemoteNow(); }, wait);
    }

    /// <summary>Не чаще одного легкого перечитывания по чужим правкам за этот промежуток.</summary>
    private static readonly TimeSpan RemoteGap = TimeSpan.FromSeconds(5);

    private DateTime _remoteLast = DateTime.MinValue;
    private bool     _remotePending;

    private void RefreshRemoteNow()
    {
        _remoteLast = DateTime.UtcNow;
        // Сосед мог проставить или сменить тип документа — посчитанные гистограммы устарели.
        ForgetHistograms();
        RefreshCurrent(quiet: true, light: true);
    }

    /// <summary>Обновить набор смонтированных БД (live add/remove) без переподписки обработчиков.</summary>
    public void SetDatabases(IReadOnlyList<DatabaseManager> databases)
    {
        SetDatabaseList(databases);
        ForgetHistograms();
        RefreshCurrent();
    }

    public void Refresh()
    {
        ForgetHistograms();
        RefreshCurrent();
    }

    private void SetDatabaseList(IReadOnlyList<DatabaseManager> databases)
    {
        _dbs.Clear();
        foreach (var d in databases) _dbs.Add((d.DbId, d.Name, d.Constructor));
    }

    private void OnLibrarySettingsChanged()
    {
        if (DocIndexService.Current is { } s) s.MaxFileMb = LibrarySettings.IndexMaxMb;

        // Все настройки перечитывают ленту тихо, и она остается на том же документе — и после
        // смены размера карточки, когда высота каждой строки другая (так решил пользователь).
        // Прежнее смещение в пикселях при этом не врет: виртуальная панель переводит его в номер
        // строки своей оценкой высоты, а оценка до перестройки — прежняя. Раньше размер
        // перечитывал ленту громко «с начала» — так было написано, а на деле лента и тогда
        // оставалась на месте. Сторож — стенд разделов («шрифт и размер карточки держат место»).
        if (_dbs.Count > 0) RefreshCurrent(quiet: true);
    }

    private void OnFontScaleChanged()
    {
        if (_dbs.Count > 0) RefreshCurrent(quiet: true);
    }

    // ── Подвал блока ─────────────────────────────────────────────────────────

    /// <summary>
    /// Пересобрать строку подвала. Текст считает чистый <see cref="DocStatus"/>, здесь только
    /// сбор входных данных и привязка ссылки к действию.
    /// </summary>
    private void RefreshStatus()
    {
        var service = DocIndexService.Current;
        var reading = service?.Running == true;

        var line = DocStatus.Compose(new DocStatusInput(
            Census:        _census,
            Reading:       reading,
            ReadDone:      _readDone,
            ReadTotal:     _readTotal,
            Left:          _readLeft,
            Query:         _searchQuery.Trim(),
            InText:        _foundInText,
            ByTitle:       _foundByTitle,
            Capped:        _foundCapped,
            CacheOpen:     service is not null,
            TypesNarrowed: _types is not null,
            Loading:       _loading,
            TextFailed:    _textFailed));

        DocList.SetStatus(line.Text, line.ActionText, line.Action switch
        {
            DocStatusAction.Read => () => _ = ReadDocumentsAsync(),
            DocStatusAction.Stop => () => DocIndexService.Current?.Stop(),
            _                    => null,
        });
    }

    // ── Загрузка ─────────────────────────────────────────────────────────────

    /// <param name="quiet">
    /// Тихое обновление: лента остается на экране и возвращается на прежнее место, оверлей
    /// «Загрузка…» не показывается. Так перечитываются данные ПОСЛЕ действия — человек смотрит
    /// на карточку, а не начинает все заново.
    /// </param>
    /// <param name="light">
    /// Легкое перечитывание по чужой правке (<see cref="RefreshRemote"/>): диск спрашивается
    /// только о новых путях, полной переписи нет.
    /// </param>
    private void RefreshCurrent(bool quiet = false, bool light = false)
    {
        // Скрытый раздел не читает (то же правило, что у «Задач» и календаря): лента по всем
        // базам на старте шла по разу на каждую подключенную базу. Отметка остается, раздел
        // перечитается при показе; громкое обновление среди отложенных побеждает тихое. Легкое
        // при этом не откладывается: при показе раздел перечитается целиком.
        if (!IsVisible)
        {
            _loadDeferred   = true;
            _deferredQuiet &= quiet;
            return;
        }
        if (_loadDeferred) { quiet &= _deferredQuiet; light = false; }
        _loadDeferred  = false;
        _deferredQuiet = true;
        _ = LoadAsync(quiet, light);
    }

    private bool _loadDeferred;
    private bool _deferredQuiet = true;

    // Показ раздела: отложенное чтение — сейчас. Постом: главное окно следом за IsVisible
    // отдает свежие галочки (SetActiveIds), и тогда чтение пойдет одно и уже с ними.
    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible && _loadDeferred)
            Dispatcher.UIThread.Post(() => { if (_loadDeferred && IsVisible) RefreshCurrent(); },
                                     DispatcherPriority.Background);
    }

    private async Task LoadAsync(bool quiet = false, bool light = false)
    {
        DataStamp = ChangeBus.Now();
        if (_dbs.Count == 0)
        {
            // Поколение сдвигаем и здесь: загрузка, начатая когда базы еще были, иначе дочитала
            // бы свое и показала документы поверх пустого экрана. Оверлей гасим сами — гасить
            // его больше некому, ту загрузку мы только что обогнали.
            _gen++;
            _loading = false;
            ShowEmpty();
            return;
        }

        var gen       = ++_gen;
        var oldOffset = DocList.ScrollOffset;
        using var slow = Logging.Diag.Measure("загрузка раздела «Библиотека документов»",
                                              Logging.Diag.UiOpen, $"баз {_dbs.Count}");

        // Прежний виртуальный список выбрасываем: его фоновые страницы больше никто не покажет,
        // а подключения они занимать продолжали бы.
        _virtual?.Cancel();
        _virtual = null;
        _thumbAsked.Clear();

        var work = _searchQuery.Trim().Length > 0 ? LoadSearchAsync(gen) : LoadFeedAsync(gen, light);

        // Оверлей — только если не успели за 400 мс И эта загрузка все еще актуальна: правило
        // целиком в DocLoadPlan, там же его «почему» и тесты.
        var timedOut = await Task.WhenAny(work, Task.Delay(400)) != work;
        if (DocLoadPlan.ShouldShowLoading(quiet, timedOut, gen, _gen))
        {
            _loading = true;
            RefreshStatus();
        }

        try { await work; }
        catch (Exception ex)
        {
            if (!DocLoadPlan.IsCurrent(gen, _gen)) return;
            if (Logging.Diag.Throttle("library-load-fail", TimeSpan.FromMinutes(1)))
                Serilog.Log.Warning(ex, "Библиотека: лента не прочитана");
            DocList.ShowNotice("Документы не прочитались: база занята или недоступна.");
        }
        finally
        {
            if (DocLoadPlan.ShouldHideLoading(gen, _gen)) { _loading = false; RefreshStatus(); }
        }

        if (!DocLoadPlan.IsCurrent(gen, _gen)) return;
        if (quiet) DocList.ScrollOffset = oldOffset;

        // Подвал показывает то, что известно СЕЙЧАС, и обновится сам, когда досчитается перепись.
        RefreshStatus();
        _ = light ? RefreshCensusLightAsync() : RefreshCensusAsync();
    }

    // Обычная лента: размеры потоков считаются в фоне, дальше — либо все в память, либо страницы.
    private async Task LoadFeedAsync(int gen, bool light = false)
    {
        // Легкое перечитывание берет отметки файлов у карточек на экране: чужая правка меняет
        // базу, а не файлы, и спрашивать диск заново о тех же путях незачем. Это касается ленты
        // в памяти — страничная и так спрашивает диск лишь о видимых страницах.
        var shown   = light ? ShownStamps() : null;
        var view    = new ConstructorRepository.DocView(_types);
        var entries = new List<DocVirtualList.Entry>();
        var total   = 0;

        foreach (var (dbId, dbName, repo) in _dbs)
        {
            var active = ActiveFor(dbId);
            if (active is { Count: 0 }) continue;

            // Без фильтра типов размер — дешевый счет по индексу. С фильтром — сумма по
            // гистограмме типов: она уже посчитана выпадашкой, через которую фильтр и ставят.
            var stats = view.IsAll
                ? await repo.GetDocCollectionStatsAsync(active, view)
                : (await HistogramsAsync(dbId, repo, active)).ToDictionary(
                      p => p.Key,
                      p => new ConstructorRepository.DocCollectionStats(p.Value.CountFor(view), p.Value.TypeFieldId));
            if (gen != _gen) return;

            foreach (var (colId, s) in stats.OrderBy(p => p.Key))
            {
                total += s.Total;
                entries.Add(new DocVirtualList.Entry
                {
                    DbId        = dbId,
                    DbName      = _dbs.Count > 1 ? dbName : "",
                    ColId       = colId,
                    Repo        = repo,
                    TypeFieldId = s.TypeFieldId ?? 0,
                    View        = view,
                    Docs        = s.Total,
                });
            }
        }
        if (gen != _gen) return;

        if (total == 0) { ShowEmpty(); return; }

        // Небольшой раздел целиком в памяти: там дешевле выделение и не нужны фоновые страницы.
        if (DocLoadPlan.NeedsDocRead(total, DocLoadCap))
        {
            var cards = new List<object>(total);
            foreach (var e in entries)
            {
                var part = await e.Repo.GetDocPageAsync(e.ColId, e.TypeFieldId, e.View, 0, e.Docs);
                if (gen != _gen) return;
                // Файлы спрашиваются в пуле, а не карточкой в UI-потоке: здесь их до десяти тысяч, и
                // на сетевой папке раздел висел бы на них при каждом открытии.
                await Task.Run(() => Probe(part, shown));
                if (gen != _gen) return;
                foreach (var d in part)
                {
                    d.DbId   = e.DbId;
                    d.DbName = e.DbName;
                    ApplyTextState(d);
                    var card = new DocCardVm(d);
                    cards.Add(card);
                    RequestThumbnail(card);
                }
            }
            DocList.Show(cards);
            return;
        }

        var list = new DocVirtualList(entries, DocList.Selection.Keys);
        list.CardRealized += RequestThumbnail;
        list.TextState     = ApplyTextState;
        _virtual = list;
        DocList.Show(list);
    }

    // Отметки файлов карточек на экране (лента в памяти): путь → время правки и размер.
    private Dictionary<string, (long Mtime, long Size)> ShownStamps()
    {
        var stamps = new Dictionary<string, (long Mtime, long Size)>(StringComparer.Ordinal);
        foreach (var card in DocList.Cards)
            if (card.Doc is { OnDiskKnown: true, Path.Length: > 0 } doc)
                stamps[doc.Path] = (doc.Mtime, doc.SizeBytes);
        return stamps;
    }

    // Отметки файлов: известные — с экрана, остальные — с диска.
    private static void Probe(List<DocRecord> docs, Dictionary<string, (long Mtime, long Size)>? shown)
    {
        if (shown is not { Count: > 0 }) { DocStamps.Probe(docs); return; }

        var fresh = new List<DocRecord>();
        foreach (var doc in docs)
        {
            if (doc.Path.Length > 0 && shown.TryGetValue(doc.Path, out var stamp))
            {
                (doc.Mtime, doc.SizeBytes) = stamp;
                doc.OnDiskKnown = true;
            }
            else fresh.Add(doc);
        }
        if (fresh.Count > 0) DocStamps.Probe(fresh);
    }

    // Поиск: сначала кеш текста (он отдает пути и сниппеты), потом базы — они сшивают пути с
    // записями и заодно ищут по названию. Выдача ограничена (см. пояснение в DocsPaging), и
    // потолок один на раздел: остаток идет по базам по очереди, а признак обрезки — не догадка
    // по круглому числу, а лишняя строка сверх остатка (DocSearchBudget).
    private async Task LoadSearchAsync(int gen)
    {
        var query  = _searchQuery.Trim();
        var view   = new ConstructorRepository.DocView(_types);
        var budget = new DocSearchBudget(SearchLimit);
        // Длину цитаты берем от размера карточки: на шесть строк четырнадцати слов не хватает,
        // а на одну — наоборот, подсветка уехала бы за многоточие.
        var words = DocTiles.Of(LibrarySettings.CardSize).SnippetWords;
        // Кеш спрашивается на одну находку больше потолка: лишняя и есть признак, что по
        // содержимому нашлось больше, чем можно показать, — и подвал скажет про хвост.
        List<DocHit> hits = [];
        var textFailed = false;
        if (DocIndexService.Current is { } service)
            try { hits = await service.Index.SearchAsync(query, budget.Ask, words); }
            catch (Exception ex)
            {
                // Поиск внутри не сработал (кеш испорчен, занят, пропал) — по названию все равно
                // ищем: базы целы, и человек получит эту половину ответа вместе с подвалом,
                // который говорит, что она — половина. Раньше сбой кеша выглядел как «ничего
                // не найдено».
                textFailed = true;
                if (Logging.Diag.Throttle("library-text-search-fail", TimeSpan.FromMinutes(1)))
                    Serilog.Log.Warning(ex, "Библиотека: поиск внутри документов не выполнен");
            }
        if (gen != _gen) return;
        if (hits.Count > budget.Limit)
        {
            budget.MarkCapped();
            hits.RemoveRange(budget.Limit, hits.Count - budget.Limit);
        }

        var snippets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var h in hits) snippets.TryAdd(h.Path, h.Snippet);
        var paths = snippets.Keys.ToList();

        var found   = new List<object>();
        var inText  = 0;
        var byTitle = 0;
        foreach (var (dbId, dbName, repo) in _dbs)
        {
            // Потолок исчерпан — остальные базы не спрашиваются: показать их находки негде.
            if (budget.Ask == 0) break;
            var active = ActiveFor(dbId);
            if (active is { Count: 0 }) continue;

            // Фильтр типов стоит в самом запросе, чтобы потолок считался ПОСЛЕ него. Снятые
            // галочки при этом работают и в поиске — иначе они молча переставали бы
            // действовать, как только человек что-то набрал.
            var part = await repo.SearchDocsAsync(active, query, paths, view, budget.Ask);
            if (gen != _gen) return;
            var take = budget.Accept(part.Count);
            if (take < part.Count) part.RemoveRange(take, part.Count - take);

            await Task.Run(() => DocStamps.Probe(part));
            if (gen != _gen) return;

            foreach (var d in part)
            {
                d.DbId   = dbId;
                d.DbName = _dbs.Count > 1 ? dbName : "";
                // Сниппет есть только у совпадений по содержимому: найденное по названию
                // показывается без него, и это честно — внутри документа слова может не быть.
                var hitInText = snippets.TryGetValue(DocTextIndex.KeyOf(d.Path), out var snip);
                if (hitInText) d.Snippet = snip ?? "";

                ApplyTextState(d);
                var card = new DocCardVm(d);
                found.Add(card);
                RequestThumbnail(card);
                // Разбивка «внутри текста против по названию» — главное число раздела, и оно
                // бесплатно: сниппет есть ровно у попаданий из содержимого.
                if (hitInText) inText++; else byTitle++;
            }
        }
        if (gen != _gen) return;

        _foundInText  = inText;
        _foundByTitle = byTitle;
        _foundCapped  = budget.Capped;
        _textFailed   = textFailed;

        DocList.Show(found, EmptySearchText());
        // Про потолок выдачи говорит подвал блока — общий баннер ошибок для этого не нужен:
        // обрезанный список это не ошибка.
        DocList.HideNotice();
    }

    private string EmptySearchText()
    {
        if (DocIndexService.Current is null)
            return "Ничего не найдено.\nПоиск по содержимому недоступен: кеш документов не открылся.";

        // Внутрь документов поиск не заглянул — «ничего не найдено» про них было бы неправдой.
        if (_textFailed)
            return "Ничего не найдено по названию.\nПоиск внутри документов не сработал — " +
                   "подробности в разделе «Диагностика».";

        return "Ничего не найдено.\nПоиск по содержимому работает для " +
               $"{LibrarySettings.SupportedFormats}; сканы без текстового слоя не находятся.";
    }

    private void ShowEmpty()
    {
        DocList.Show(new List<object>(),
                     "Нет документов.\nВключите тумблер «Добавить в библиотеку документов» " +
                     "у свойства типа «Файл» в конструкторе базы.");
    }

    private ISet<int>? ActiveFor(int dbId) =>
        _activeByDb is null ? null
        : _activeByDb.TryGetValue(dbId, out var ids) ? ids
        : new HashSet<int>();   // база есть, но ни одной отмеченной коллекции

    private async Task<List<(string Value, int Count)>> LoadTypeCountsAsync()
    {
        var all = new List<ConstructorRepository.DocTypeHistogram>();
        foreach (var (dbId, _, repo) in _dbs.ToList())
        {
            var active = ActiveFor(dbId);
            if (active is { Count: 0 }) continue;
            all.AddRange((await HistogramsAsync(dbId, repo, active)).Values);
        }
        return ConstructorRepository.MergeTypeCounts(all);
    }

    /// <summary>
    /// Гистограммы типов отмеченных коллекций базы: посчитанные берутся из кеша, недостающие
    /// считаются в фоне и ложатся в кеш — если он не был сброшен, пока шел счет.
    /// </summary>
    private async Task<Dictionary<int, ConstructorRepository.DocTypeHistogram>> HistogramsAsync(
        int dbId, ConstructorRepository repo, ISet<int>? active)
    {
        var gen = _histogramsGen;
        // Копия, а не сам кеш: счет идет в фоне, а кеш тем временем пополняет UI-поток.
        var known = _histograms.TryGetValue(dbId, out var cached)
            ? new Dictionary<int, ConstructorRepository.DocTypeHistogram>(cached)
            : null;

        var result = await repo.GetDocTypeHistogramsAsync(active, known);

        if (gen == _histogramsGen)
        {
            if (!_histograms.TryGetValue(dbId, out var cache)) _histograms[dbId] = cache = [];
            foreach (var (colId, h) in result) cache[colId] = h;
        }
        return result;
    }

    private void ForgetHistograms()
    {
        _histograms.Clear();
        _histogramsGen++;
    }

    // ── Миниатюры ────────────────────────────────────────────────────────────

    // Картинка для карточки: готовая из кеша — сразу, иначе фоновый рендер. Один путь просим
    // один раз за жизнь ленты: одна и та же книга бывает в нескольких записях.
    private void RequestThumbnail(DocCardVm card)
    {
        if (!LibrarySettings.Thumbnails) return;
        if (card.IsPlaceholder || card.Doc.Path.Length == 0) return;
        if (!DocThumbnail.IsSupported(card.Doc.Path)) return;

        var path = card.Doc.Path;
        var gen  = _gen;

        // Отметка документа уже снята в фоне вместе со страницей — в UI-потоке остается один
        // вопрос к ЛОКАЛЬНОЙ папке кеша, а не к серверу, где лежит сам документ.
        if (DocThumbnail.Cached(AppSettings.DocThumbsDir, path, card.Doc.Mtime, card.Doc.SizeBytes) is { } ready)
        {
            LoadInto(card, ready);
            return;
        }
        if (!_thumbAsked.Add(DocTextIndex.KeyOf(path))) return;

        _ = Task.Run(async () =>
        {
            await _thumbGate.WaitAsync();
            try
            {
                var file = DocThumbnail.Render(AppSettings.DocThumbsDir, path);
                if (file is null || gen != _gen) return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (gen != _gen) return;
                    LoadInto(card, file);
                });
            }
            catch { /* миниатюра второстепенна: не вышло — останется бейдж формата */ }
            finally { _thumbGate.Release(); }
        });
    }

    private static void LoadInto(DocCardVm card, string file)
    {
        try { card.SetThumbnail(new Bitmap(file)); }
        catch { /* битый файл кеша — останется плитка формата; следующий рендер перезапишет */ }
    }

    // ── Состояние текста на карточках ────────────────────────────────────────

    /// <summary>
    /// Проставить документу то, что кеш знает про его текст: серый чип и число страниц.
    ///
    /// Ни кеш, ни диск здесь не спрашиваются: ответ готов в карте <see cref="_states"/>, которую
    /// собрала перепись одним обходом в фоне, — вместе со сверкой времени правки файла. Раньше
    /// эту сверку делала каждая карточка сама, в UI-потоке, — по вопросу к диску на карточку, а
    /// на сетевой папке это прокрутка, встающая на каждой странице.
    ///
    /// Пока перепись не готова, состояние остается <see cref="DocTextState.Unknown"/> и чипа
    /// просто нет: пустое место честнее, чем «не прочитан» у прочитанного. Документа нет в
    /// переписи (он появился после нее) — то же самое: чип придет со следующей.
    /// </summary>
    private void ApplyTextState(DocRecord doc)
    {
        if (doc.Path.Length == 0 || _states is null) return;
        if (!_states.TryGetValue(DocTextIndex.KeyOf(doc.Path), out var state)) return;

        doc.TextState = state.State;
        doc.Pages     = state.Pages;
    }

    /// <summary>
    /// Догнать уже показанные карточки, когда перепись досчиталась. Лента строится раньше, чем
    /// готова карта кеша, поэтому чипы и страницы приезжают на место вторым заходом — так же,
    /// как приезжают миниатюры.
    /// </summary>
    private void ApplyTextStateToShownCards()
    {
        if (_virtual is { } list) { list.ReapplyTextState(ApplyTextState); return; }

        foreach (var item in DocList.Cards)
        {
            ApplyTextState(item.Doc);
            item.RefreshTextState();
        }
    }

    // ── Чтение документов ────────────────────────────────────────────────────
    //
    // Автозапуска НЕТ и настройки «читать автоматически» тоже нет. Раньше очередь стартовала при
    // каждом заходе в раздел — то есть программа без спроса тратила часы процессора и диска на
    // разбор всего, что отмечено. Теперь чтение начинает человек: кнопкой в подвале блока или
    // пунктом меню коллекции в сайдбаре.

    /// <summary>
    /// Пути документов отмеченных коллекций. Ходит в базы, поэтому только в фоне: на сотнях
    /// тысяч документов один этот запрос заметен.
    ///
    /// База не ответила — исключение уходит зовущему, а не становится пустым списком. Раньше
    /// недоступная база молча давала «ноль документов»: перепись считала числа подвала без нее, а
    /// «Прочитать документы» отвечало «все документы уже прочитаны», ничего у нее не спросив.
    /// Теперь перепись остается прежней и пишет об отказе в журнал, а чтение говорит, что база
    /// занята или недоступна, — как и лента, которая без этой базы не собирается.
    /// </summary>
    private async Task<List<string>> ActivePathsAsync()
    {
        var dbs   = _dbs.ToList();
        var byDb  = _activeByDb;
        var paths = new List<string>();

        foreach (var (dbId, _, repo) in dbs)
        {
            var active = byDb is null ? null
                       : byDb.TryGetValue(dbId, out var ids) ? ids : new HashSet<int>();
            if (active is { Count: 0 }) continue;
            paths.AddRange(await repo.LoadDocPathsAsync(active));
        }
        return paths;
    }

    /// <summary>
    /// Перепись состояний текста по отмеченным коллекциям — числа для подвала и чипы карточек.
    /// Считается ПОСЛЕ показа ленты и в фоне: обход спрашивает у диска время правки каждого
    /// файла, и держать на этом открытие раздела нельзя.
    ///
    /// Переписи идут по одной (<see cref="DocRunGate"/>): касание галочки перезапускает ленту, а
    /// с ней и перепись, и пять касаний подряд раньше давали пять обходов диска разом, из которых
    /// нужен был только последний.
    /// </summary>
    private async Task RefreshCensusAsync()
    {
        if (!_censusGate.Request()) return;
        do
        {
            try { await CensusOnceAsync(); }
            catch (Exception ex)
            {
                // Не ответила база или кеш — прежние числа и чипы остаются: неполная перепись
                // показала бы числа без документов той базы, которая промолчала.
                if (Logging.Diag.Throttle("library-census-fail", TimeSpan.FromMinutes(1)))
                    Serilog.Log.Warning(ex, "Библиотека: перепись документов не выполнена");
            }
        }
        while (_censusGate.Done());
    }

    private async Task CensusOnceAsync()
    {
        if (DocIndexService.Current is not { } service) { RefreshStatus(); return; }

        var gen    = _gen;
        var paths  = await ActivePathsAsync();
        var survey = await Task.Run(() => service.Index.Survey(paths, LibrarySettings.IndexMaxMb));

        // Пока шли на диск, лента сменилась или перепись попросили заново — этот итог про
        // прежний набор документов, и показывать его нельзя.
        if (_censusGate.Stale || gen != _gen) return;

        _census = survey.Census;
        _states = survey.Files;
        ApplyTextStateToShownCards();
        RefreshStatus();

        // Перепись нашла на диске документы — значит диск отвечает, и уборке кеша есть на что
        // опереться: строки об удаленных и переименованных документах можно выбросить. Раз за
        // запуск программы, в фоне, и не во время чтения — служба следит за этим сама.
        //
        // Ни одного файла не нашлось (галочки сняты, папка отвалилась) — уборку не начинаем:
        // попытка за запуск одна, и тратить ее на заход, когда диск молчит, незачем.
        if (survey.Census.Total > 0) _ = service.SweepOnceAsync();
    }

    /// <summary>
    /// Легкая перепись после чужой правки (<see cref="RefreshRemote"/>). Пути берутся из баз
    /// заново — это запрос к базе, а не к диску, — а диск спрашивается только о новых: тех, кого
    /// нет в карте состояний прошлой переписи. Числа подвала пересчитываются по карте
    /// (<see cref="DocCensus.Of"/>) — по тем же правилам, что у полной переписи, поэтому чипы и
    /// числа по-прежнему из одного источника, а убранный соседом документ из чисел уходит.
    /// Измененный на диске файл легкая перепись не заметит — это дело полной, при следующем
    /// заходе в раздел: чужая правка меняет базу, а не файлы.
    ///
    /// Карты еще нет (полная перепись не досчиталась) — дополнять нечего, идет полная.
    /// </summary>
    private async Task RefreshCensusLightAsync()
    {
        if (_states is not { } states || DocIndexService.Current is not { } service)
        {
            await RefreshCensusAsync();
            return;
        }

        var gen = _gen;
        try
        {
            var paths  = await ActivePathsAsync();
            var fresh  = paths.Where(p => !states.ContainsKey(DocTextIndex.KeyOf(p))).ToList();
            var survey = fresh.Count > 0 ? await Task.Run(() => service.Index.Survey(fresh, LibrarySettings.IndexMaxMb)) : null;
            if (gen != _gen) return;

            var merged = new Dictionary<string, DocFileState>(states, StringComparer.Ordinal);
            if (survey is not null)
                foreach (var (key, state) in survey.Files) merged[key] = state;

            _states = merged;
            _census = DocCensus.Of(paths.Select(p => merged.GetValueOrDefault(DocTextIndex.KeyOf(p),
                                                                              new DocFileState(DocTextState.NotRead, 0))));
            ApplyTextStateToShownCards();
            RefreshStatus();
        }
        catch (Exception ex)
        {
            if (Logging.Diag.Throttle("library-census-fail", TimeSpan.FromMinutes(1)))
                Serilog.Log.Warning(ex, "Библиотека: перепись документов не выполнена");
        }
    }

    /// <summary>
    /// Поиск заодно дочитывает непрочитанное. «Найти» — это просьба найти ВНУТРИ документов, и
    /// разделять ее на два нажатия («найди» и отдельно «а теперь прочитай») незачем: человек
    /// уже сказал, чего хочет.
    ///
    /// Сама выдача при этом НЕ ждет чтения: она строится сразу по тому, что уже разобрано, а
    /// чтение идет фоном и по окончании само перечитывает выдачу (<c>OnIndexFinished</c>).
    /// Ждать разбора двадцати тысяч документов ради одного запроса человек не подписывался.
    ///
    /// Порог подтверждения по времени остается на общем пути: долгая работа спрашивает, даже
    /// когда ее начал поиск, — «Найти» не должно оказаться кнопкой на полчаса машины.
    ///
    /// Сверяемся с последней переписью, а не идем на диск: полный обход стоит по обращению к
    /// диску на каждый файл, и платить им за каждое нажатие «Найти» нельзя. Перепись еще не
    /// готова или читать нечего — просто выходим; предложение прочитать в этом случае никуда
    /// не девается, оно стоит кнопкой в подвале.
    ///
    /// Идущий прогон поиск не трогает вовсе, хотя очередь добавление на ходу умеет: он и так
    /// прочитает все непрочитанное, а каждое «Найти» платило бы за проверку полным обходом
    /// кеша и диска.
    /// </summary>
    private Task ReadOnSearchAsync()
    {
        if (DocIndexService.Current is not { Running: false }) return Task.CompletedTask;
        if (_census.NotRead <= 0) return Task.CompletedTask;

        return ReadDocumentsAsync();
    }

    /// <summary>
    /// Прочитать документы одной коллекции — из меню сайдбара. Охват уже, чем у кнопки в
    /// подвале: работать обычно приходится с одной папкой, и разбирать ради нее всю библиотеку
    /// незачем.
    /// </summary>
    public void ReadCollection(int dbId, int collectionId)
    {
        var repo = _dbs.FirstOrDefault(d => d.DbId == dbId).Repo;
        if (repo is null) return;
        _ = ReadDocumentsAsync(() => repo.LoadDocPathsAsync(new HashSet<int> { collectionId }));
    }

    /// <summary>
    /// Прочитать документы. Большой объем спрашивает подтверждение: человек должен знать, во
    /// что ввязывается, прежде чем машина уйдет на полчаса работы.
    /// </summary>
    /// <param name="source">Откуда брать пути; по умолчанию — все отмеченные коллекции.</param>
    private async Task ReadDocumentsAsync(Func<Task<List<string>>>? source = null)
    {
        if (DocIndexService.Current is not { } service) return;

        Follow(service);
        service.MaxFileMb = LibrarySettings.IndexMaxMb;

        List<string> paths;
        try { paths = await (source is null ? ActivePathsAsync() : source()); }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Библиотека: список документов для чтения не собран");
            DocList.ShowNotice("Документы не прочитаны: база занята или недоступна.");
            return;
        }

        List<string> pending;
        try
        {
            // Перепись пересчитываем только для полного охвата: у одной коллекции она не про
            // всю ленту, и подменять ею числа подвала было бы неправдой.
            var survey = await Task.Run(() => service.Index.Survey(paths, LibrarySettings.IndexMaxMb));
            pending = survey.Pending;
            if (source is null)
            {
                _census = survey.Census;
                _states = survey.Files;
                ApplyTextStateToShownCards();
            }
        }
        catch (Exception ex)
        {
            // Базы ответили, а кеш — нет. Сказать «база занята» значило бы послать человека
            // чинить не то.
            Serilog.Log.Warning(ex, "Библиотека: кеш документов не ответил перед чтением");
            DocList.ShowNotice("Документы не прочитаны: кеш документов недоступен — " +
                               "подробности в разделе «Диагностика».");
            return;
        }

        if (pending.Count == 0)
        {
            RefreshStatus();
            // Молчать нельзя: человек нажал кнопку и обязан узнать, что она сработала, — иначе
            // «Обновить» выглядит неисправной. Это СОБЫТИЕ, а не состояние раздела, поэтому
            // карточка, а не строка подвала: подвал говорит о том, что мешает искать сейчас.
            Controls.Widgets.AppNotifications.Info(
                "Изменений нет — все документы уже прочитаны.");
            return;
        }

        if (LibrarySettings.NeedsReadConfirmation(pending.Count) && !await ConfirmRead(pending.Count))
            return;

        // Прогон уже идет — пути просто добавляются к нему: очередь это умеет, а раньше просьба
        // молча пропадала. Прочитать документы во время чтения просят из меню коллекции: в
        // подвале на это время стоит «Остановить».
        //
        // Счетчики при добавлении НЕ обнуляются: разобранное в этом прогоне так и осталось
        // разобранным, а новый размер очереди приедет со следующим событием хода.
        var running = service.Running;
        if (!running)
        {
            _readDone  = 0;
            _readTotal = pending.Count;
            _readLeft  = null;
            Core.BackgroundJobs.Report(ReadJobId, ReadJobTitle, $"0 из {pending.Count}",
                                       0, pending.Count, () => DocIndexService.Current?.Stop());
        }
        var added = service.StartPending(pending);
        RefreshStatus();

        // Карточка-событие про запуск: человек нажал «Прочитать» и тут же может уйти в другой
        // раздел — он должен знать, где за этим следить.
        Controls.Widgets.AppNotifications.Info(DocStatus.ReadStartedNotice(running, pending.Count, added));
    }

    // Окно подтверждения — общий ConfirmDialog (Compact по стандарту размеров окон), а не свое.
    private async Task<bool> ConfirmRead(int count)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return true;

        var estimate = LibrarySettings.SecondsPerDocument is { } perDoc && perDoc > 0
            ? $"Это займет {DocStatus.Humanize(TimeSpan.FromSeconds(perDoc * count))}. "
            : "Чтение займет заметное время. ";

        var dlg = new Controls.Dialogs.ConfirmDialog(
            $"Прочитать {Plural.Count(count, "документ", "документа", "документов")}?\n\n" +
            estimate +
            "Программа продолжит работать: чтение идет фоном, и его можно остановить в любой " +
            "момент — прочитанное не пропадет.",
            confirmText: "Прочитать");

        return await dlg.ShowDialog<bool>(owner);
    }

    /// <summary>Ключ работы в реестре фоновых работ — он же ключ обновления ее строки в панели.</summary>
    private const string ReadJobId = "library.read";

    private const string ReadJobTitle = "Чтение документов";

    private void OnIndexProgress(object? sender, DocIndexProgress p)
    {
        // В реестр отчитываемся СРАЗУ, не дожидаясь UI-потока: значок в панели разделов должен
        // жить, даже когда раздел закрыт и его карточки никто не перерисовывает.
        var detail = $"{p.Done} из {p.Total}" +
                     (p.Left is { } left ? $" · осталось {DocStatus.Humanize(left)}" : "");
        Core.BackgroundJobs.Report(ReadJobId, ReadJobTitle, detail,
                                   p.Done, p.Total, () => DocIndexService.Current?.Stop());

        Dispatcher.UIThread.Post(() =>
        {
            _readDone  = p.Done;
            _readTotal = p.Total;
            _readLeft  = p.Left;
            DocList.SetProgress(p.Done, p.Total);
            RefreshStatus();
        });
    }

    private void OnIndexFinished(object? sender, DocIndexRunEnd end)
    {
        // Сорвавшаяся работа остается в реестре — красной, с причиной: человек, давно ушедший из
        // раздела, иначе не узнал бы, почему чтение кончилось на половине. Уходит она ссылкой
        // «Убрать» в панели фоновых работ или следующим запуском чтения. Отчитываемся сразу, из
        // потока очереди, — как и о ходе.
        if (end.Failed)
            Core.BackgroundJobs.Fail(ReadJobId, ReadJobTitle, DocStatus.ReadFailedDetail(end));
        else
            Core.BackgroundJobs.Finish(ReadJobId);

        Dispatcher.UIThread.Post(() =>
        {
            DocList.SetProgress(0, 0);
            // Подвал — сразу, а не после переписи: сорвавшееся чтение часто значит, что и кеш не
            // отвечает, перепись тогда не досчитается, и подвал остался бы на «Читаю документы»
            // со ссылкой «Остановить» у работы, которой уже нет.
            RefreshStatus();
            if (DocIndexService.Current is { LastSecondsPerDocument: > 0 } s)
                LibrarySettings.RememberSpeed(s.LastSecondsPerDocument);

            // Карточка-событие: человек мог уйти из раздела и не увидеть, что чтение кончилось.
            // Живой ход живет в значке панели разделов, а сюда идут только события.
            if (end.Failed)
                Controls.Widgets.AppNotifications.Error(DocStatus.ReadFailedNotice(end));
            else if (end.Done > 0)
                Controls.Widgets.AppNotifications.Info(DocStatus.ReadDoneNotice(end.Done, _census.NeverSearchable));

            // Перепись устарела: только что прочитанное поменяло и числа подвала, и чипы
            // карточек. Поиск по содержимому мог не находить того, что как раз разобрали, —
            // выдачу перечитываем; обычную ленту чтение не меняет, и трогать ее незачем.
            _ = RefreshCensusAsync();
            if (_searchQuery.Trim().Length > 0) RefreshCurrent(quiet: true);
        });
    }

    // ── Открытие документа ───────────────────────────────────────────────────

    /// <summary>
    /// Открыть документ системой — тем же способом, что ячейка типа «Файл»
    /// (<c>Controls/Widgets/FileEditor.axaml.cs</c>). Запрет <c>BannedSymbols.txt</c> действует
    /// на путь ВХОДЯЩИХ данных («наружу одна дверь»); открытие по клику человека — не он.
    /// </summary>
    /// <summary>Текущий запрос поиска — панель чтения открывается сразу на нем.</summary>
    public string SearchQuery => _searchQuery;

    /// <summary>Отразить состояние панели чтения на кнопке навбара.</summary>
    public void SetReaderOpen(bool isOpen) => NavbarControl.SetReaderOpen(isOpen);

    private void RaiseQuery() => SearchQueryChanged?.Invoke(this, _searchQuery);

    private void OpenDocument(DocRecord doc)
    {
        if (doc.Path.Length == 0) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = doc.Path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Библиотека: документ не открылся системой: {Path:l}", doc.Path);
            DocList.ShowNotice("Документ не открылся: файл недоступен или для него нет программы.");
        }
    }
}
