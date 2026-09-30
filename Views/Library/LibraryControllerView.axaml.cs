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

    /// <summary>Размер карточки на момент последнего чтения — по нему видно, что настройка
    /// сменила именно РАЗМЕР, и прокрутку восстанавливать нельзя.</summary>
    private DocCardSize _cardSize = LibrarySettings.CardSize;

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

        if (DocIndexService.Current is { } service)
        {
            service.Progress += OnIndexProgress;
            service.Finished += OnIndexFinished;
        }

        RefreshCurrent();
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
        if (_dbs.Count == 0) return;

        // Смена размера карточки меняет высоту КАЖДОЙ строки, а тихое обновление возвращает
        // прокрутку в пикселях — прежнее смещение указывало бы уже на другой документ. Поэтому
        // размер обновляет ленту громко, с начала, а остальные настройки — по-прежнему тихо.
        var resized = _cardSize != LibrarySettings.CardSize;
        _cardSize   = LibrarySettings.CardSize;
        RefreshCurrent(quiet: !resized);
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
            Loading:       _loading));

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
    private void RefreshCurrent(bool quiet = false)
    {
        // Скрытый раздел не читает (то же правило, что у «Задач» и календаря): лента по всем
        // базам на старте шла по разу на каждую подключенную базу. Отметка остается, раздел
        // перечитается при показе; громкое обновление среди отложенных побеждает тихое.
        if (!IsVisible)
        {
            _loadDeferred   = true;
            _deferredQuiet &= quiet;
            return;
        }
        if (_loadDeferred) quiet &= _deferredQuiet;
        _loadDeferred  = false;
        _deferredQuiet = true;
        _ = LoadAsync(quiet);
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

    private async Task LoadAsync(bool quiet = false)
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

        var work = _searchQuery.Trim().Length > 0 ? LoadSearchAsync(gen) : LoadFeedAsync(gen);

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
        _ = RefreshCensusAsync();
    }

    // Обычная лента: размеры потоков считаются в фоне, дальше — либо все в память, либо страницы.
    private async Task LoadFeedAsync(int gen)
    {
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
                await Task.Run(() => DocStamps.Probe(part));
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

    // Поиск: сначала кеш текста (он отдает пути и сниппеты), потом база — она сшивает пути с
    // записями и заодно ищет по названию. Выдача ограничена: см. пояснение в DocsPaging.
    private async Task LoadSearchAsync(int gen)
    {
        var query = _searchQuery.Trim();
        // Длину цитаты берем от размера карточки: на шесть строк четырнадцати слов не хватает,
        // а на одну — наоборот, подсветка уехала бы за многоточие.
        var words = DocTiles.Of(LibrarySettings.CardSize).SnippetWords;
        var hits  = DocIndexService.Current is { } service
            ? await service.Index.SearchAsync(query, SearchLimit, words)
            : [];
        if (gen != _gen) return;

        var snippets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var h in hits) snippets.TryAdd(h.Path, h.Snippet);
        var paths = snippets.Keys.ToList();

        var found   = new List<object>();
        var shown   = 0;
        var inText  = 0;
        var byTitle = 0;
        foreach (var (dbId, dbName, repo) in _dbs)
        {
            var active = ActiveFor(dbId);
            if (active is { Count: 0 }) continue;

            var part = await repo.SearchDocsAsync(active, query, paths, SearchLimit);
            if (gen != _gen) return;
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

                // Фильтр типов действует и в поиске: иначе снятые галочки молча переставали бы
                // работать, как только человек что-то набрал.
                if (_types is not null && !_types.Contains(d.DocType)) continue;

                ApplyTextState(d);
                var card = new DocCardVm(d);
                found.Add(card);
                RequestThumbnail(card);
                shown++;
                // Разбивка «внутри текста против по названию» — главное число раздела, и оно
                // бесплатно: сниппет есть ровно у попаданий из содержимого.
                if (hitInText) inText++; else byTitle++;
            }
        }
        if (gen != _gen) return;

        _foundInText  = inText;
        _foundByTitle = byTitle;
        _foundCapped  = shown >= SearchLimit;

        DocList.Show(found, EmptySearchText());
        // Про потолок выдачи говорит подвал блока — общий баннер ошибок для этого не нужен:
        // обрезанный список это не ошибка.
        DocList.HideNotice();
    }

    private string EmptySearchText()
    {
        var indexed = DocIndexService.Current is not null;
        return indexed
            ? "Ничего не найдено.\nПоиск по содержимому работает для " +
              $"{LibrarySettings.SupportedFormats}; сканы без текстового слоя не находятся."
            : "Ничего не найдено.\nПоиск по содержимому недоступен: кеш документов не открылся.";
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
            try { paths.AddRange(await repo.LoadDocPathsAsync(active)); }
            catch { /* база занята — посчитаем при следующем заходе в раздел */ }
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
                if (Logging.Diag.Throttle("library-census-fail", TimeSpan.FromMinutes(1)))
                    Serilog.Log.Warning(ex, "Библиотека: состояние кеша документов не прочитано");
            }
        }
        while (_censusGate.Done());
    }

    private async Task CensusOnceAsync()
    {
        if (DocIndexService.Current is not { } service) { RefreshStatus(); return; }

        var gen    = _gen;
        var paths  = await ActivePathsAsync();
        var survey = await Task.Run(() => service.Index.Survey(paths));

        // Пока шли на диск, лента сменилась или перепись попросили заново — этот итог про
        // прежний набор документов, и показывать его нельзя.
        if (_censusGate.Stale || gen != _gen) return;

        _census = survey.Census;
        _states = survey.Files;
        ApplyTextStateToShownCards();
        RefreshStatus();
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

        service.MaxFileMb = LibrarySettings.IndexMaxMb;

        List<string> pending;
        try
        {
            var paths = await (source is null ? ActivePathsAsync() : source());
            // Перепись пересчитываем только для полного охвата: у одной коллекции она не про
            // всю ленту, и подменять ею числа подвала было бы неправдой.
            var survey = await Task.Run(() => service.Index.Survey(paths));
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
            Serilog.Log.Warning(ex, "Библиотека: список документов для чтения не собран");
            DocList.ShowNotice("Документы не прочитаны: база занята или недоступна.");
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
            Core.BackgroundJobs.Report(ReadJobId, "Чтение документов", $"0 из {pending.Count}",
                                       0, pending.Count, () => DocIndexService.Current?.Stop());
        }
        var added = service.StartPending(pending);
        RefreshStatus();

        // Карточка-событие про запуск: человек нажал «Прочитать» и тут же может уйти в другой
        // раздел — он должен знать, где за этим следить.
        Controls.Widgets.AppNotifications.Info(
            !running
                ? $"Читаю {Plural.Count(pending.Count, "документ", "документа", "документов")}. " +
                  "Можно работать дальше — ход виден в панели разделов слева."
                : added > 0
                    ? $"Добавлено к чтению: {Plural.Count(added, "документ", "документа", "документов")}. " +
                      "Читаю их тем же прогоном."
                    : "Эти документы уже стоят в очереди чтения.");
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

    private void OnIndexProgress(object? sender, DocIndexProgress p)
    {
        // В реестр отчитываемся СРАЗУ, не дожидаясь UI-потока: значок в панели разделов должен
        // жить, даже когда раздел закрыт и его карточки никто не перерисовывает.
        var detail = $"{p.Done} из {p.Total}" +
                     (p.Left is { } left ? $" · осталось {DocStatus.Humanize(left)}" : "");
        Core.BackgroundJobs.Report(ReadJobId, "Чтение документов", detail,
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

    private void OnIndexFinished(object? sender, EventArgs e)
    {
        Core.BackgroundJobs.Finish(ReadJobId);
        var read = _readDone;

        Dispatcher.UIThread.Post(() =>
        {
            DocList.SetProgress(0, 0);
            if (DocIndexService.Current is { LastSecondsPerDocument: > 0 } s)
                LibrarySettings.RememberSpeed(s.LastSecondsPerDocument);

            // Карточка-событие: человек мог уйти из раздела и не увидеть, что чтение кончилось.
            // Живой ход живет в значке панели разделов, а сюда идут только события.
            if (read > 0)
                Controls.Widgets.AppNotifications.Info(
                    $"Документы прочитаны: {read}. " +
                    (_census.NeverSearchable > 0
                        ? $"Внутри {_census.NeverSearchable} поиск искать не будет: " +
                          "сканы без текстового слоя и форматы без чтения текста."
                        : "Поиск теперь ищет внутри них."));

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
