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

    // Перепись документов по состоянию текста и сама карта «путь → что про него знает кеш».
    // Считается в фоне ПОСЛЕ показа ленты: обход кеша ходит на диск за временем правки каждого
    // файла, и держать на нем открытие раздела нельзя.
    private DocCensus _census;
    private Dictionary<string, DocIndexEntry> _known = new(StringComparer.Ordinal);

    // Итоги последнего поиска — из них собирается строка подвала.
    private int  _foundInText;
    private int  _foundByTitle;
    private bool _foundCapped;

    // Ход идущего чтения — приходит событиями очереди, живет здесь ради строки подвала.
    private int       _readDone;
    private int       _readTotal;
    private TimeSpan? _readLeft;

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

        DocList.DocActivated    += (_, d) => OpenDocument(d);
        DocList.SearchRequested += (_, q) => { _searchQuery = q; RefreshCurrent(); };
        DocList.SearchCleared   += (_, _) => { _searchQuery = ""; RefreshCurrent(); };

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

        var quiet = force && DocLoadPlan.SameActiveIds(_activeByDb, activeByDb);
        _activeByDb = activeByDb;
        RefreshCurrent(quiet);
    }

    /// <summary>Обновить набор смонтированных БД (live add/remove) без переподписки обработчиков.</summary>
    public void SetDatabases(IReadOnlyList<DatabaseManager> databases)
    {
        SetDatabaseList(databases);
        RefreshCurrent();
    }

    public void Refresh() => RefreshCurrent();

    private void SetDatabaseList(IReadOnlyList<DatabaseManager> databases)
    {
        _dbs.Clear();
        foreach (var d in databases) _dbs.Add((d.DbId, d.Name, d.Constructor));
    }

    private void OnLibrarySettingsChanged()
    {
        if (DocIndexService.Current is { } s) s.MaxFileMb = LibrarySettings.IndexMaxMb;
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
            TypesNarrowed: _types is not null));

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
    private void RefreshCurrent(bool quiet = false) => _ = LoadAsync(quiet);

    private async Task LoadAsync(bool quiet = false)
    {
        DataStamp = ChangeBus.Now();
        if (_dbs.Count == 0)
        {
            // Поколение сдвигаем и здесь: загрузка, начатая когда базы еще были, иначе дочитала
            // бы свое и показала документы поверх пустого экрана. Оверлей гасим сами — гасить
            // его больше некому, ту загрузку мы только что обогнали.
            _gen++;
            LoadingOverlay.IsVisible = false;
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
        if (DocLoadPlan.ShouldShowOverlay(quiet, timedOut, gen, _gen))
            LoadingOverlay.IsVisible = true;

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
            if (DocLoadPlan.ShouldHideOverlay(gen, _gen)) LoadingOverlay.IsVisible = false;
        }

        if (!DocLoadPlan.IsCurrent(gen, _gen)) return;
        if (quiet) DocList.ScrollOffset = oldOffset;

        // Подвал показывает то, что известно СЕЙЧАС, и обновится сам, когда досчитается перепись.
        RefreshStatus();
        _ = RefreshCensusAsync(gen);
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

            var stats = await repo.GetDocCollectionStatsAsync(active, view);
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
        var hits  = DocIndexService.Current is { } service
            ? await service.Index.SearchAsync(query, SearchLimit)
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
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (dbId, _, repo) in _dbs)
        {
            var active = ActiveFor(dbId);
            if (active is { Count: 0 }) continue;

            foreach (var (value, count) in await repo.GetDocTypeCountsAsync(active))
                totals[value] = totals.GetValueOrDefault(value) + count;
        }
        // Порядок как в выпадашке колонки таблицы: по убыванию количества, «(Пусто)» последним.
        return [.. totals.OrderBy(p => p.Key.Length == 0)
                         .ThenByDescending(p => p.Value)
                         .ThenBy(p => p.Key, StringComparer.CurrentCulture)
                         .Select(p => (p.Key, p.Value))];
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

        if (DocThumbnail.Cached(AppSettings.DocThumbsDir, path) is { } ready)
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
    /// Спрашивать кеш на каждую карточку отдельно нельзя — это сотни запросов на экран, —
    /// поэтому ответ берется из карты <see cref="_known"/>, собранной одним обходом. Пока карта
    /// не готова, состояние остается <see cref="DocTextState.Unknown"/> и чипа просто нет:
    /// пустое место честнее, чем «не прочитан» у прочитанного.
    /// </summary>
    private void ApplyTextState(DocRecord doc)
    {
        if (doc.Path.Length == 0 || _known.Count == 0) return;

        var key = DocTextIndex.KeyOf(doc.Path);
        if (!_known.TryGetValue(key, out var entry))
        {
            doc.TextState = DocTextState.NotRead;
            return;
        }

        // Файл изменился после разбора — в кеше лежит текст прежней редакции.
        var (mtime, size) = DocTextIndex.Stamp(key);
        if (size >= 0 && (entry.Mtime != mtime || entry.Size != size))
        {
            doc.TextState = DocTextState.NotRead;
            return;
        }

        doc.Pages     = entry.Pages;
        doc.TextState = entry.Status switch
        {
            DocTextStatus.Ok          => DocTextState.Read,
            DocTextStatus.NoText      => DocTextState.NoText,
            DocTextStatus.Unsupported => DocTextState.Unsupported,
            _                         => DocTextState.Failed,
        };
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
    /// Перепись состояний текста по отмеченным коллекциям — числа для подвала и чипов карточек.
    /// Считается ПОСЛЕ показа ленты и в фоне: обход спрашивает у диска время правки каждого
    /// файла, и держать на этом открытие раздела нельзя.
    /// </summary>
    private async Task RefreshCensusAsync(int gen)
    {
        if (DocIndexService.Current is not { } service) { RefreshStatus(); return; }

        try
        {
            var paths = await ActivePathsAsync();
            if (gen != _gen) return;

            var (_, census) = await Task.Run(() => service.Index.Survey(paths));
            var known       = await Task.Run(() => service.Index.Known([.. paths.Select(DocTextIndex.KeyOf)]));
            if (gen != _gen) return;

            _census = census;
            _known  = known;
            ApplyTextStateToShownCards();
            RefreshStatus();
        }
        catch (Exception ex)
        {
            if (Logging.Diag.Throttle("library-census-fail", TimeSpan.FromMinutes(1)))
                Serilog.Log.Warning(ex, "Библиотека: состояние кеша документов не прочитано");
        }
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
        if (service.Running) return;

        service.MaxFileMb = LibrarySettings.IndexMaxMb;

        List<string> pending;
        try
        {
            var paths = await (source is null ? ActivePathsAsync() : source());
            // Перепись пересчитываем только для полного охвата: у одной коллекции она не про
            // всю ленту, и подменять ею числа подвала было бы неправдой.
            var survey = await Task.Run(() => service.Index.Survey(paths));
            pending = survey.Pending;
            if (source is null) _census = survey.Census;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Библиотека: список документов для чтения не собран");
            DocList.ShowNotice("Документы не прочитаны: база занята или недоступна.");
            return;
        }

        if (pending.Count == 0) { RefreshStatus(); return; }

        if (LibrarySettings.NeedsReadConfirmation(pending.Count) && !await ConfirmRead(pending.Count))
            return;

        _readDone  = 0;
        _readTotal = pending.Count;
        _readLeft  = null;
        Core.BackgroundJobs.Report(ReadJobId, "Чтение документов", $"0 из {pending.Count}",
                                   0, pending.Count, () => DocIndexService.Current?.Stop());
        service.StartPending(pending);
        RefreshStatus();

        // Карточка-событие про запуск: человек нажал «Прочитать» и тут же может уйти в другой
        // раздел — он должен знать, где за этим следить.
        Controls.Widgets.AppNotifications.Info(
            $"Читаю {Plural.Count(pending.Count, "документ", "документа", "документов")}. " +
            "Можно работать дальше — ход виден в панели разделов слева.");
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
            _ = RefreshCensusAsync(_gen);
            if (_searchQuery.Trim().Length > 0) RefreshCurrent(quiet: true);
        });
    }

    // ── Открытие документа ───────────────────────────────────────────────────

    /// <summary>
    /// Открыть документ системой — тем же способом, что ячейка типа «Файл»
    /// (<c>Controls/Widgets/FileEditor.axaml.cs</c>). Запрет <c>BannedSymbols.txt</c> действует
    /// на путь ВХОДЯЩИХ данных («наружу одна дверь»); открытие по клику человека — не он.
    /// </summary>
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
