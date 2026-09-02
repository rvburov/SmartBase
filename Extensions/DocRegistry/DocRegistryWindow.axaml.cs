using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Serilog;
using SmartBase.Controls.Dialogs;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>
/// Окно расширения «Регистрация КД» (в основном стиле приложения): ФИО регистрирующего, строка
/// обозначения, наименование, классификатор, обязательный проект, два примечания, база и
/// коллекция журнала. По «Зарегистрировать» документ получает обозначение и строку в коллекции.
///
/// Обозначение собрано в ОДИН РЯД из своих частей и с теми же разделителями, что и в готовом
/// номере: код организации, код ЕСКД, номер, исполнение — строка читается как
/// «АБВГ.301121.012-01». Номер выдает расширение, поле недоступно; исполнение выбирает человек
/// из списка 01–99, где занятые помечены.
///
/// Номер зависит от того, выбрано ли исполнение. Без исполнения это первый свободный номер серии
/// — новый документ. С исполнением — ДЕЙСТВУЮЩИЙ номер: исполнение приписывают к существующему
/// документу, а не заводят им новый (см. <see cref="NumberFor"/>).
///
/// Второй способ работы — ЗАГРУЗКА ФАЙЛОМ (список сверху): окно заносит в журнал целый список
/// разом, выдавая номера по тем же правилам. Тогда все поля документа гаснут — значения берутся
/// из файла, — а рамка классификатора показывает разбор файла: ошибки с номерами строк или то,
/// что будет записано. Чтение и проверка — <see cref="RegistryFile"/> и
/// <see cref="RegistryImport"/>, запись — <see cref="DocRegistrar.RegisterAll"/>.
///
/// Окно НЕ закрывается после записи: документы регистрируют пачками, поэтому ФИО, код
/// организации, база и коллекция остаются, а код ЕСКД, наименование, проект и примечания
/// очищаются под следующий документ. Записали исполнение — не очищается ничего, кроме самого
/// исполнения: документ тот же, меняется только его вариант. ФИО и коллекция переживают и
/// закрытие окна — лежат в настройках расширения (ключи <c>ext.doc-registry.…</c>, уборка при
/// удалении расширения идет по приставке).
///
/// Все тяжелое — не здесь: справочник разбирает <see cref="EskdCatalog"/> (распаковка заранее в
/// фоне, подбор — тоже в фоне, чтобы ввод не подтормаживал), структуру коллекции проверяет
/// <see cref="RegistrySchema"/>, запись делает <see cref="DocRegistrar"/>. Сама регистрация идет
/// синхронно в UI-потоке: одна запись и семь значений — фон тут не нужен, а на общем репозитории
/// срабатывают проверки прав.
/// </summary>
public partial class DocRegistryWindow : Window
{
    // Базы реестра в порядке пунктов DbCombo (индекс пункта → DbId).
    private readonly IReadOnlyList<(int DbId, string Name)> _databases;

    // Коллекции выбранной базы в порядке пунктов CollectionCombo.
    private List<DbCollection> _collections = [];

    private ConstructorRepository? _repo;
    private RegistryShape?         _shape;
    private string?                _schemaProblem;

    // Обозначения выбранной коллекции: читаются один раз при ее выборе и пополняются своими
    // записями. Нужны только предпросмотру номера — фактический номер DocRegistrar считает
    // заново по базе, поэтому устареть этот список не страшно.
    private List<string> _designations = [];

    /// <summary>
    /// Номер, на котором окно держится, пока подряд заводят исполнения одного документа.
    /// <c>null</c> — номер обычный, первый свободный.
    ///
    /// Зачем: после записи «012-01» номер 012 занят, и первый свободный стал бы 013 — второе
    /// исполнение того же документа ушло бы к чужому номеру. Держится он только при выбранном
    /// исполнении; сняли исполнение, сменили серию или коллекцию — снова первый свободный.
    /// </summary>
    private int? _heldNumber;

    /// <summary>Список исполнений перестраивается — не считать это выбором человека.</summary>
    private bool _rebuildingExecutions;

    // Номер последнего запущенного подбора: ответ с чужим номером — устаревший и не применяется.
    private int  _searchToken;
    private bool _suppressSearch;
    private bool _busy;

    // Коды организации из настроек. В ручном режиме из них выбирают, при загрузке файлом по ним
    // же проверяются обозначения — список нужен обоим, поэтому лежит полем, а не только в ComboBox.
    private readonly IReadOnlyList<string> _orgs;

    // Разобранный файл со списком номеров: строки как есть и итог их проверки. Строки хранятся,
    // чтобы перепроверить их после записи (и не читать книгу заново).
    private IReadOnlyList<ImportRow> _rows = [];
    private ImportCheck?             _check;

    // Файл не прочитался целиком (не тот формат, не те столбцы) — причина показывается вместо
    // разбора строк. От ошибок В СТРОКАХ отличается тем, что проверять там нечего.
    private string? _fileProblem;

    /// <summary>
    /// Сколько строк записано из этого файла; <c>null</c> — еще не записан.
    ///
    /// Сторожит от повторной записи. Номера теперь выдает расширение, поэтому второе нажатие
    /// кнопки завело бы те же документы еще раз, уже под другими номерами, и ничто бы этому не
    /// помешало: повторов, на которых спотыкался бы разбор, тут нет по устройству. Записанный
    /// файл поэтому снимается с разбора целиком — чтобы занести его снова, за него надо взяться
    /// заново.
    /// </summary>
    private int? _written;

    // Номер последнего запущенного чтения файла — то же назначение, что у _searchToken.
    private int _fileToken;

    public DocRegistryWindow()
    {
        InitializeComponent();

        // Справочник распаковывается в фоне, и как только готов — открывается на классах: дальше
        // вглубь ведут щелчок по строке и ввод цифр кода. До готовности в рамке стоит подпись из
        // разметки («Справочник ЕСКД загружается…»).
        _ = ShowCatalogWhenReady();

        // Доступ к базам — через ту же точку, что и межбазовые связи: расширению не нужно знать
        // ни о Workspace, ни о главном окне (тот же прием, что в «Документах в коллекцию»).
        _databases = RelationDbResolver.Databases();
        DbCombo.ItemsSource = _databases.Select(d => d.Name).ToList();
        DbCombo.IsEnabled   = _databases.Count > 1;   // выбирать не из чего — список заблокирован

        // Коды организации объявлены настройкой расширения (экран — «Настройки → Расширения»):
        // у каждого предприятия они свои, и зашивать их в код нельзя.
        _orgs = DocRegistryExtension.ParseOrgCodes(
            AppSettings.LoadExtensionSetting(DocRegistryExtension.ExtensionId,
                                             DocRegistryExtension.OrgCodesKey));
        OrgCombo.ItemsSource   = _orgs;
        OrgCombo.IsEnabled     = _orgs.Count > 0;
        OrgCombo.SelectedIndex = _orgs.Count > 0 ? 0 : -1;

        // ФИО — тоже настройка расширения: у машины оно одно и меняется редко. Окно подставляет
        // сохраненное и после регистрации пишет обратно то, что стоит в поле, — значение общее
        // с экраном настроек.
        FioBox.Text = AppSettings.LoadExtensionSetting(DocRegistryExtension.ExtensionId,
                                                       DocRegistryExtension.FioKey);

        // Способ регистрации. По умолчанию ручной: файлом заносят старые списки, а изо дня в
        // день выдают номера по одному. Ни способ, ни путь к файлу между запусками не помнятся —
        // это выбор под задачу, а не настройка рабочего места.
        ModeCombo.ItemsSource   = new[] { ManualMode, FileMode };
        ModeCombo.SelectedIndex = 0;

        // Выбор базы тянет за собой список коллекций и проверку структуры.
        RestoreDatabase();

        UpdateState();
    }

    // ── Куда пишем: база и коллекция ──────────────────────────────────────────

    private void OnDbChanged(object? sender, SelectionChangedEventArgs e)
    {
        _repo        = null;
        _collections = [];
        CollectionCombo.ItemsSource = null;

        var index = DbCombo.SelectedIndex;
        if (index >= 0 && index < _databases.Count)
        {
            try
            {
                _repo = RelationDbResolver.RepoFor(_databases[index].DbId);
                if (_repo is not null)
                {
                    _collections = _repo.GetAllCollections();
                    CollectionCombo.ItemsSource = _collections.Select(c => c.Name).ToList();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Регистрация КД: список коллекций базы не прочитан");
                _repo = null;
            }
        }

        CollectionCombo.IsEnabled = _collections.Count > 0;
        RestoreLastCollection();

        // Ничего не выбралось — SelectionChanged не поднимется, раскладку сбрасываем сами.
        if (CollectionCombo.SelectedIndex < 0) RefreshShape();
    }

    // База журнала у человека одна и та же изо дня в день — возвращаем прошлую по ИМЕНИ.
    // Не по DbId: он живет в реестре и после переустановки или перемонтирования баз может
    // значить другое, а имя человек и видит в списке. Не нашлась — первая, как и раньше:
    // без базы окно бесполезно, а с одной базой этот путь вообще единственный.
    private void RestoreDatabase()
    {
        if (_databases.Count == 0) return;

        var last = AppSettings.LoadExtensionSetting(DocRegistryExtension.ExtensionId,
                                                    DocRegistryExtension.DatabaseKey);

        var at = -1;
        if (last.Length > 0)
            for (var i = 0; i < _databases.Count && at < 0; i++)
                if (string.Equals(_databases[i].Name, last, StringComparison.CurrentCultureIgnoreCase))
                    at = i;

        DbCombo.SelectedIndex = at >= 0 ? at : 0;
    }

    // Коллекция журнала — так же по имени. Не нашлась (переименовали, другая база) — не выбираем
    // НИЧЕГО: наугад выбранная первая коллекция тут же отвечала бы «Вы выбрали неправильную
    // коллекцию» на пустом месте.
    private void RestoreLastCollection()
    {
        if (_collections.Count == 0) { CollectionCombo.SelectedIndex = -1; return; }

        var last = AppSettings.LoadExtensionSetting(DocRegistryExtension.ExtensionId,
                                                    DocRegistryExtension.CollectionKey);

        CollectionCombo.SelectedIndex = last.Length == 0
            ? -1
            : _collections.FindIndex(c => string.Equals(c.Name, last, StringComparison.CurrentCultureIgnoreCase));
    }

    private void OnCollectionChanged(object? sender, SelectionChangedEventArgs e) => RefreshShape();

    /// <summary>
    /// Перечитать выбранную коллекцию: сошлась ли структура и какие обозначения в ней уже выданы.
    /// Обозначения читаются ОДИН раз на выбор коллекции — дальше их пополняет сама регистрация,
    /// а фактический номер движок все равно считает заново по базе.
    /// </summary>
    private void RefreshShape()
    {
        _shape         = null;
        _schemaProblem = null;
        _designations  = [];
        _heldNumber    = null;   // другой журнал — держаться не за что

        var index = CollectionCombo.SelectedIndex;
        if (_repo is not null && index >= 0 && index < _collections.Count)
        {
            var collection = _collections[index];
            try
            {
                var check = RegistrySchema.Check(_repo, collection.Id!.Value, collection.Name);
                if (check.Shape is { } shape)
                {
                    _shape        = shape;
                    _designations = [.. DocRegistrar.Designations(_repo, shape)];
                    RememberTarget(collection.Name);
                }
                else
                {
                    _schemaProblem = check.Problem;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Регистрация КД: коллекция «{Collection}» не прочитана", collection.Name);
                _schemaProblem = ex.Message;
            }
        }

        // Журнал сменился — разбор файла к нему больше не относится: занятые обозначения в
        // другой коллекции свои. Книгу заново не читаем, строки для этого и хранятся.
        RecheckFile();

        UpdateState();
    }

    /// <summary>
    /// Запомнить приемник — базу и коллекцию — в настройках расширения. Записывается ТОЛЬКО
    /// когда структура коллекции сошлась: незачем возвращать в следующий раз выбор, на котором
    /// работать все равно нельзя.
    /// </summary>
    private void RememberTarget(string collectionName)
    {
        var index = DbCombo.SelectedIndex;
        if (index >= 0 && index < _databases.Count)
            AppSettings.SaveExtensionSetting(DocRegistryExtension.ExtensionId,
                                             DocRegistryExtension.DatabaseKey,
                                             _databases[index].Name);

        AppSettings.SaveExtensionSetting(DocRegistryExtension.ExtensionId,
                                         DocRegistryExtension.CollectionKey,
                                         collectionName);
    }

    // ── Загрузка файлом ───────────────────────────────────────────────────────

    /// <summary>Пункты списка способов регистрации. Первый — по умолчанию.</summary>
    private const string ManualMode = "Ручное регистрирование номеров КД";
    private const string FileMode   = "Загрузить файл со списком номеров КД";

    /// <summary>Строк разбора в рамке. Ошибок в чужом файле бывает столько же, сколько строк, а
    /// список тут не виртуализованный — да и читать тысячу строк подряд все равно никто не станет:
    /// правят по первым, потом перечитывают файл.</summary>
    private const int ReportLimit = 200;

    /// <summary>Пауза перед разбором: путь можно вписать руками, и разбор на каждую букву был бы
    /// чтением книги на каждое нажатие клавиши.</summary>
    private static readonly TimeSpan FilePause = TimeSpan.FromMilliseconds(300);

    private static readonly FilePickerFileType ExcelType =
        new($"Excel ({string.Join(", ", RegistryFile.Extensions.Select(e => "*" + e))})")
        {
            Patterns = [.. RegistryFile.Extensions.Select(e => "*" + e)],
        };

    /// <summary>Работаем со списком из файла, а не с одним документом.</summary>
    private bool ByFile => ModeCombo.SelectedIndex == 1;

    private void OnModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        ApplyMode();

        // Разбор прошлого файла в ручном режиме ни при чем, а при возврате к файлу его надо
        // прочитать заново: журнал за это время мог пополниться руками.
        _ = ReloadFile();
    }

    /// <summary>
    /// Развести окно по способу регистрации: работает ровно то, чем в этом способе пользуются,
    /// остальное гаснет. Поля не прячутся, а именно гаснут — человек должен видеть, что окно то
    /// же самое и что значения этих полей теперь берутся из файла.
    /// </summary>
    private void ApplyMode()
    {
        var byFile = ByFile;

        FileBox.IsEnabled   = byFile;
        BrowseBtn.IsEnabled = byFile;

        FioBox.IsEnabled         = !byFile;
        OrgCombo.IsEnabled       = !byFile && _orgs.Count > 0;
        EskdBox.IsEnabled        = !byFile;
        ExecutionCombo.IsEnabled = !byFile;
        NameBox.IsEnabled        = !byFile;
        ProjectBox.IsEnabled     = !byFile;
        AccountingBox.IsEnabled  = !byFile;
        FirstUseBox.IsEnabled    = !byFile;
        SummaryBox.IsEnabled     = !byFile;

        // Рамка одна на двоих: классификатор нужен только при ручном подборе кода, разбор
        // файла — только при загрузке.
        FileReport.IsVisible   = byFile;
        SuggestList.IsVisible  = !byFile && SuggestList.ItemCount > 0;
        SuggestEmpty.IsVisible = !byFile && SuggestList.ItemCount == 0;
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Выберите файл со списком номеров КД",
            AllowMultiple  = false,
            FileTypeFilter = [ExcelType],
        });

        if (files.FirstOrDefault()?.Path.LocalPath is not { } path) return;

        // Тот же файл выбрали второй раз — значит, его поправили и просят перечитать. Текст поля
        // при этом не меняется, и сам по себе разбор бы не запустился.
        if (string.Equals(FileBox.Text, path, StringComparison.Ordinal)) _ = ReloadFile();
        else FileBox.Text = path;
    }

    private void OnFilePathChanged(object? sender, TextChangedEventArgs e) => _ = ReloadFile();

    /// <summary>
    /// Прочитать файл и проверить его против журнала. Ответ устаревшего чтения (путь успели
    /// сменить) отбрасывается по номеру — как и у подбора кода ЕСКД.
    /// </summary>
    private async Task ReloadFile()
    {
        var token = ++_fileToken;

        _rows        = [];
        _check       = null;
        _fileProblem = null;
        _written     = null;

        ShowReport();
        UpdateState();

        var path = (FileBox.Text ?? "").Trim();
        if (!ByFile || path.Length == 0) return;

        await Task.Delay(FilePause);
        if (token != _fileToken) return;

        try
        {
            // В фоне: чтение с диска, разбор книги и распаковка zip внутри нее — окно на это
            // время замирать не должно.
            var rows = await Task.Run(() => RegistryFile.Read(path));
            if (token != _fileToken) return;

            _rows  = rows;
            _check = RegistryImport.Check(rows, _designations, _orgs);
        }
        catch (DocRegistryException ex)
        {
            if (token != _fileToken) return;
            _fileProblem = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Регистрация КД: файл со списком номеров не прочитан");
            if (token != _fileToken) return;
            _fileProblem = $"Файл не прочитан: {ex.Message}";
        }

        ShowReport();
        UpdateState();
    }

    /// <summary>Перепроверить уже прочитанные строки — после смены журнала и после записи.
    /// Книга при этом не читается: строки для того и хранятся.</summary>
    private void RecheckFile()
    {
        if (_rows.Count == 0) return;

        _check = RegistryImport.Check(_rows, _designations, _orgs);
        ShowReport();
    }

    /// <summary>
    /// Показать разбор файла в рамке: причину, по которой файл не читается, список ошибок или
    /// перечень того, что будет записано.
    /// </summary>
    private void ShowReport()
    {
        if (_fileProblem is { } trouble) { ShowLines([trouble]); return; }
        if (_check is not { } check)     { ShowLines([]);        return; }

        ShowLines(check.Problems.Count > 0
            ? Limited(check.Problems.Select(p => p.ToString()).ToList(), "ошибок")
            : Limited(check.Rows.Select(r => $"Строка {r.Line}: {r.Designation} — {r.Name}").ToList(),
                      "строк"));
    }

    /// <summary>Первые <see cref="ReportLimit"/> строк, а сколько осталось — последней строкой:
    /// «показано 200 из 1000» человеку ничего не говорит, а «и еще 800» говорит.</summary>
    private static List<string> Limited(List<string> lines, string what)
    {
        if (lines.Count <= ReportLimit) return lines;

        var shown = lines.Take(ReportLimit).ToList();
        shown.Add($"…и еще {what}: {lines.Count - ReportLimit}");
        return shown;
    }

    private void ShowLines(IReadOnlyList<string> lines) => FileList.ItemsSource = lines;

    /// <summary>
    /// Записать в журнал разом весь проверенный файл. Спрашиваем подтверждение: строк бывает
    /// много, а отменить запись одним движением нельзя — записи придется удалять руками.
    /// </summary>
    private async Task RegisterFromFile()
    {
        if (_check is not { Ok: true } check || check.Rows.Count == 0) return;

        if (!await new ConfirmDialog(
                $"Записать в коллекцию «{_shape!.CollectionName}» строк: {check.Rows.Count}?",
                confirmText: "Записать",
                cancelText:  "Отмена").ShowDialog<bool>(this))
            return;

        string message, brush;

        // Окно прогресса и его задача-закрытие живут дольше try: закрывать окно надо в любом
        // исходе, в том числе на ошибке и на отмене.
        ProgressDialog? progress = null;
        Task?           closed   = null;

        _busy = true;
        RegisterBtn.IsEnabled = false;
        try
        {
            // Номера выдаются ЗАНОВО и по свежему журналу: между разбором файла и нажатием
            // кнопки те же номера мог занять другой регистратор. Довод тот же, что и у ручной
            // регистрации, — только там номер один, а здесь весь список.
            _designations = [.. DocRegistrar.Designations(_repo!, _shape!)];

            var fresh = RegistryImport.Check(_rows, _designations, _orgs);
            if (!fresh.Ok || fresh.Rows.Count == 0)
                throw new DocRegistryException("Файл больше не годится к записи — смотрите разбор.");

            // Права спрашиваем ЗДЕСЬ, на общем репозитории: пишет список фоновое подключение, а
            // оно прав не несет — по уговору репозитория разрешение проверяет публичный вход.
            _repo!.Permissions?.Demand(PermAction.CreateRecord, _shape!.CollectionId);
            _repo!.Permissions?.Demand(PermAction.EditRecord,   _shape!.CollectionId);

            using var cancel = new CancellationTokenSource();

            progress = new ProgressDialog { Title = "Регистрация КД" };
            progress.SetInfo($"Строк в файле: {fresh.Rows.Count}");
            // Фаза — ДО показа, чтобы окно не меняло высоту с первым процентом (как у удаления).
            progress.SetProgress(0, "Запись в журнал…");
            progress.EnableCancel(cancel.Cancel);
            closed = progress.ShowDialog(this);

            var count = await WriteRows(fresh.Rows, progress, cancel.Token);

            // Выданные обозначения — сразу в местный список: по нему считается и предпросмотр
            // номера в ручном режиме, и номера следующего файла.
            foreach (var row in fresh.Rows) _designations.Add(row.Designation);

            Log.Information("Регистрация КД: из файла записано строк {Count} — коллекция «{Collection}»",
                            count, _shape!.CollectionName);

            // Файл занесен и с разбора снимается, а в рамке остается то, что записано, — с
            // выданными номерами: по этому списку человек и сверяется.
            _written = count;
            _rows    = [];
            _check   = null;

            ShowLines(Limited(
                [.. fresh.Rows.Select(r => $"Строка {r.Line}: {r.Designation} — {r.Name}")], "строк"));

            (message, brush) = ($"Записано строк: {count}", "BrushAccent");
        }
        catch (OperationCanceledException)
        {
            // Отмена — та же дорога, что и ошибка: записанные строки уже убраны, журнал чист.
            (message, brush) = ("Отменено — в журнал не записано ничего", "BrushSecondaryText");
        }
        catch (PermissionDeniedException ex)
        {
            (message, brush) = (ex.Message, "BrushRed");
        }
        catch (DocRegistryException ex)
        {
            Log.Warning(ex, "Регистрация КД: список из файла не записан");
            (message, brush) = (ex.Message, "BrushRed");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Регистрация КД: непредвиденная ошибка при записи списка из файла");
            (message, brush) = ($"Ошибка: {ex.Message}", "BrushRed");
        }
        finally
        {
            if (progress is not null)
            {
                progress.Done();
                if (closed is not null) await closed;
            }

            _busy = false;
        }

        // После удачной записи перепроверять нечего — строки сняты с разбора; а вот после отказа
        // разбор надо освежить: журнал мог измениться, и номера в предпросмотре уже другие.
        RecheckFile();
        UpdateState();
        SetStatus(message, brush);
    }

    /// <summary>
    /// Записать строки в фоне, показывая проценты в окне прогресса. Отдает число записанных.
    ///
    /// В фон уходит вся запись целиком, вместе со своим подключением к базе
    /// (<see cref="DocRegistrar.RegisterAllIsolated"/>): пиши мы в UI-потоке, окно прогресса было
    /// бы неподвижной картинкой — перерисовать его тому же потоку некогда.
    ///
    /// Проценты постятся не на каждую строку, а на смену целого процента: на длинном файле поток
    /// сообщений сам по себе грузит диспетчер сильнее, чем сама запись.
    /// </summary>
    private async Task<int> WriteRows(IReadOnlyList<ImportRow> rows, ProgressDialog dialog,
                                      CancellationToken ct)
    {
        var dbPath = _repo!.DbPath;
        var shape  = _shape!;
        var total  = rows.Count;
        var last   = -1;

        // Progress<T> зовет обработчик в том потоке, где создан, — то есть в UI-потоке окна.
        IProgress<int> progress = new Progress<int>(done =>
        {
            var percent = total == 0 ? 100 : done * 100 / total;
            if (percent == last) return;

            last = percent;
            dialog.SetProgress(percent / 100.0, $"Запись в журнал… ({done} из {total})");
        });

        return await Task.Run(
            () => DocRegistrar.RegisterAllIsolated(dbPath, shape, rows, DateTime.Now, progress, ct), ct);
    }

    /// <summary>Кнопка и подсказка в режиме загрузки файлом. Порядок проверок тот же, что и в
    /// ручном: сверху вниз по окну — сперва куда пишем, потом что пишем.</summary>
    private void UpdateFileState()
    {
        if (_databases.Count == 0)
        {
            Block("Нет доступной базы данных", red: true);
            return;
        }
        if (_orgs.Count == 0)
        {
            Block("Укажите коды организации: Настройки → Расширения");
            return;
        }
        if (_repo is null)
        {
            Block("База данных недоступна", red: true);
            return;
        }
        if (CollectionCombo.SelectedIndex < 0)
        {
            Block("Выберите коллекцию журнала регистрации");
            return;
        }
        if (_shape is null)
        {
            Block(_schemaProblem is { Length: > 0 } problem
                ? $"Вы выбрали неправильную коллекцию — {problem}"
                : "Вы выбрали неправильную коллекцию", red: true);
            return;
        }
        if (_written is { } written)
        {
            RegisterBtn.IsEnabled = false;
            SetStatus($"Записано строк: {written} — чтобы занести этот файл еще раз, " +
                      "выберите его заново", "BrushAccent");
            return;
        }
        if ((FileBox.Text ?? "").Trim().Length == 0)
        {
            Block("Укажите файл со списком номеров КД");
            return;
        }
        if (_fileProblem is { } trouble)
        {
            Block(trouble, red: true);
            return;
        }
        if (_check is not { } check)
        {
            Block("Файл читается…");
            return;
        }
        if (!check.Ok)
        {
            // Ошибки перечислены в рамке — тут только их число и то, чем это грозит.
            Block($"Ошибок в файле: {check.Problems.Count} — пока они не исправлены, " +
                  "не записывается ни одна строка", red: true);
            return;
        }

        RegisterBtn.IsEnabled = true;
        SetStatus($"Будет записано строк: {check.Rows.Count}", "BrushAccent");
    }

    // ── Подбор кода ЕСКД ──────────────────────────────────────────────────────

    /// <summary>
    /// Дождаться распаковки справочника и показать классы. Пока идет распаковка, в рамке стоит
    /// подпись из разметки — окном можно пользоваться (поля заполняются), список появляется,
    /// как только готов.
    /// </summary>
    private async Task ShowCatalogWhenReady()
    {
        try
        {
            await Task.Run(EskdCatalog.Warmup);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Регистрация КД: справочник ЕСКД не загрузился");
            ShowSuggestions(Nothing);
            SuggestEmpty.Text = "Справочник ЕСКД недоступен — код можно ввести цифрами вручную";
            SuggestHint.Text  = "";   // причина уже названа в рамке, «ничего не нашлось» тут ни при чем
            return;
        }

        // Пока распаковывалось, человек мог начать набирать — показываем то, что набрано.
        await RunSearch();
    }

    // Пустой список: справочник не поднялся. Вид «поиск по наименованию» тут ни при чем —
    // просто в нем нет ни узла, ни детей, о которых можно было бы что-то сказать в подсказке.
    private static EskdView Nothing => new([], EskdViewKind.Names, null, 0, 0, Exact: false);

    private void OnEskdTextChanged(object? sender, TextChangedEventArgs e)
    {
        // Сменили код — сменилась серия, и держаться за номер прежней серии нельзя.
        _heldNumber = null;

        UpdateState();
        if (_suppressSearch) return;

        _ = RunSearch();
    }

    /// <summary>
    /// Перестроить список по тому, что стоит в поле кода: цифры ведут по дереву, слово — поиск
    /// по наименованию, пусто — классы. Ответ устаревшего запроса (человек набрал дальше)
    /// отбрасывается по номеру.
    /// </summary>
    private async Task RunSearch()
    {
        var query = (EskdBox.Text ?? "").Trim();
        var token = ++_searchToken;

        EskdView view;
        try
        {
            // В фоне: ветка дерева считается мгновенно, а вот поиск по слову обходит 78 тыс.
            // наименований, и ввод от этого подрагивал бы. Первый вызов вдобавок может застать
            // распаковку справочника.
            view = await Task.Run(() => EskdCatalog.View(query));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Регистрация КД: подбор по справочнику ЕСКД не выполнен");
            if (token != _searchToken) return;
            ShowSuggestions(Nothing);
            SuggestEmpty.Text = "Справочник ЕСКД недоступен — код можно ввести цифрами вручную";
            SuggestHint.Text  = "";   // причина уже названа в рамке, «ничего не нашлось» тут ни при чем
            return;
        }

        if (token != _searchToken) return;   // человек набрал дальше — ответ устарел
        ShowSuggestions(view);
    }

    private void OnEskdKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:   MoveSuggestion(+1); e.Handled = true; break;
            case Key.Up:     MoveSuggestion(-1); e.Handled = true; break;

            // Enter делает с выбранной строкой ровно то же, что и щелчок по ней.
            case Key.Enter when SuggestList.SelectedItem is EskdEntry entry:
                Choose(entry);
                e.Handled = true;
                break;

            // Escape не прячет список, а возвращает его к классам: справочник открыт всегда,
            // и подняться в самый верх — это очистить поле кода.
            case Key.Escape when (EskdBox.Text ?? "").Length > 0:
                EskdBox.Text = "";
                e.Handled = true;
                break;
        }
    }

    private void MoveSuggestion(int delta)
    {
        var count = SuggestList.ItemCount;
        if (count == 0) return;

        var next = Math.Clamp(SuggestList.SelectedIndex + delta, 0, count - 1);
        SuggestList.SelectedIndex = next;
        SuggestList.ScrollIntoView(next);
    }

    private void OnSuggestTapped(object? sender, TappedEventArgs e)
    {
        if (SuggestList.SelectedItem is EskdEntry entry) Choose(entry);
    }

    /// <summary>
    /// Что делает щелчок (и Enter) по строке. Шестизначная — конец ветки: фиксируется в поля кода
    /// и наименования. Любая другая раскрывается на уровень вниз. Строки цепочки предков
    /// раскрываются так же — ими и поднимаются обратно наверх.
    /// </summary>
    private void Choose(EskdEntry entry)
    {
        if (entry.IsLeaf) Accept(entry);
        else              Descend(entry);
    }

    // Раскрыть узел: его код уходит в поле ввода, и список сам перестраивается на его ветку —
    // отдельного состояния «что раскрыто» окно не держит, все дерево задается одним кодом.
    private void Descend(EskdEntry node)
    {
        EskdBox.Text       = node.Code;
        EskdBox.CaretIndex = node.Code.Length;

        // Ходить по дереву удобнее с клавиатуры: после щелчка фокус возвращается в поле кода,
        // и следующий уровень берется стрелками с Enter, не отрывая рук.
        EskdBox.Focus();
    }

    // Зафиксировать выбранную строку: код идет в поле кода, наименование — в поле наименования
    // (и остается доступным для правки, как требует задание).
    private void Accept(EskdEntry entry)
    {
        _searchToken++;                 // ответы уже запущенных подборов больше не нужны

        _suppressSearch = true;
        EskdBox.Text    = entry.Code;
        _suppressSearch = false;
        EskdBox.CaretIndex = entry.Code.Length;

        NameBox.Text = entry.Name;

        // Список НЕ перестраиваем (см. _suppressSearch выше): выбранная строка остается на виду
        // и выделенной — видно, что именно зафиксировано, — а соседние коды можно тут же
        // пролистать и передумать. Это важно после поиска по слову: перестрой мы список,
        // плоская выдача схлопнулась бы в ветку, и остальные находки пропали бы.
        SuggestList.SelectedItem = entry;
        if (SuggestList.SelectedIndex >= 0) SuggestList.ScrollIntoView(SuggestList.SelectedIndex);

        SuggestHint.Text = $"Зафиксировано: {entry.Code} — {entry.Name}";

        NameBox.Focus();
        UpdateState();
    }

    private void ShowSuggestions(EskdView view)
    {
        var rows = view.Rows;

        SuggestList.ItemsSource = rows;

        // В режиме файла рамку занимает разбор списка номеров: подбор, доехавший из фона, не
        // должен показываться поверх него — вернется вместе с ручным режимом (см. ApplyMode).
        SuggestList.IsVisible = !ByFile && rows.Count > 0;

        // Выделяем не первую строку, а ПЕРВОГО РЕБЕНКА: в дереве наверху стоит цепочка предков,
        // и выделение на ней означало бы, что Enter поднимает обратно, а не ведет вглубь. Так
        // по дереву можно идти вниз одним Enter. Детей нет (дошли до шестизначной) — выделяем
        // последнюю строку, то есть сам код.
        SuggestList.SelectedIndex = rows.Count == 0
            ? -1
            : view.Kind == EskdViewKind.Tree ? rows.Count - Math.Max(view.ChildCount, 1) : 0;

        // Прокрутка всегда к началу: цепочка предков должна быть видна целиком — по ней человек
        // и понимает, где находится.
        if (rows.Count > 0) SuggestList.ScrollIntoView(0);

        SuggestEmpty.IsVisible = !ByFile && rows.Count == 0;
        SuggestEmpty.Text      = "Ничего не нашлось — попробуйте другое слово или введите код цифрами";

        SuggestHint.Text = HintFor(view);
    }

    /// <summary>Подпись под списком: где мы в дереве или сколько нашлось по слову.</summary>
    private string HintFor(EskdView view)
    {
        if (view.Kind == EskdViewKind.Names)
            return view.Total == 0
                ? "Ничего не нашлось"
                : $"Найдено: {view.Total} — щелчок по строке с точками откроет ее ветку";

        var typed  = (EskdBox.Text ?? "").Trim();
        var levels = EskdCatalog.LevelName(EskdCatalog.RootLength);

        // Числа ставим ПОСЛЕ существительного («классов: 43»): иначе пришлось бы склонять его
        // под каждое число, а подпись должна читаться при любом.
        if (view.Node is not { } node)
            return typed.Length == 0
                ? $"Классификатор ЕСКД, {levels}: {view.Rows.Count} — щелкните строку, чтобы раскрыть"
                : view.Rows.Count < EskdCatalog.Roots.Count
                    ? $"Классификатор ЕСКД, {levels} на «{typed}»: {view.Rows.Count}"
                    : $"Кода {typed} в справочнике нет — открыты все {levels}";

        if (!view.Exact)
            return $"Кода {typed} в справочнике нет — открыта ближайшая ветка {node.Code}";

        return view.ChildCount == 0
            ? $"{node.Code} {node.Title} — дальше не раскрывается, код полный"
            : $"{node.Code} {node.Title} — {EskdCatalog.LevelName(node.Code.Length + 1)}: {view.ChildCount}";
    }

    // ── Регистрация ───────────────────────────────────────────────────────────

    private async void OnRegisterClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _repo is null || _shape is null) return;

        if (ByFile) { await RegisterFromFile(); return; }

        // Занятое исполнение — не отказ, а вопрос: бывает, что документ в журнал занесли
        // задним числом или строку завели ошибочно, и решать это вправе только человек.
        // Подтверждение спрашиваем ЗДЕСЬ, а не в движке: движок без подтверждения откажет.
        if (!await ConfirmDuplicateExecution()) return;

        var input = Collect();
        string message, brush;

        // Записали исполнение — документ остался тот же: поля не чистятся и курсор не уходит.
        var sameDocument = false;

        _busy = true;
        RegisterBtn.IsEnabled = false;
        try
        {
            var done = DocRegistrar.Register(_repo, _shape, input, DateTime.Now);

            // Свежее обозначение — сразу в местный список: и предпросмотр следующего номера, и
            // пометки «занято» в списке исполнений считаются по нему, а не по базе.
            _designations.Add(done.Designation);

            AppSettings.SaveExtensionSetting(DocRegistryExtension.ExtensionId,
                                             DocRegistryExtension.FioKey, input.Fio.Trim());

            Log.Information("Регистрация КД: {Designation} — коллекция «{Collection}»",
                            done.Designation, _shape.CollectionName);

            // Номер записанного документа окно держит за собой: следующее исполнение припишется
            // именно к нему. Держать надо и после обычной регистрации — последний выданный в
            // серии может быть и не этим (номер выдается в дырку: заняты 001, 002 и 014 — новый
            // документ получит 003, а последний в серии так и останется 014).
            _heldNumber = done.Number;

            if (done.Execution is not null)
                ExecutionCombo.SelectedIndex = Designation.FirstFreeExecution(
                    _designations, input.Org.Trim(), input.Eskd.Trim(), done.Number) ?? -1;

            sameDocument = done.Execution is not null;

            ClearDocumentFields(keepDocument: sameDocument);
            (message, brush) = ($"Зарегистрировано: {done.Designation}", "BrushAccent");
        }
        catch (PermissionDeniedException ex)
        {
            (message, brush) = (ex.Message, "BrushRed");
        }
        catch (DocRegistryException ex)
        {
            Log.Warning(ex, "Регистрация КД: не выполнено");
            (message, brush) = (ex.Message, "BrushRed");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Регистрация КД: непредвиденная ошибка");
            (message, brush) = ($"Ошибка: {ex.Message}", "BrushRed");
        }
        finally
        {
            _busy = false;
        }

        // Сперва общее состояние (кнопка, номер и список исполнений), затем итог регистрации —
        // он важнее подсказки и должен остаться на виду.
        UpdateState();
        SetStatus(message, brush);

        // Курсор — в то поле, с которого начинается следующий документ. Записали исполнение —
        // документ тот же, код ЕСКД остался прежним, и возвращаться в него незачем.
        if (!sameDocument) EskdBox.Focus();
    }

    /// <summary>
    /// Спросить подтверждение, если выбранное исполнение уже зарегистрировано. <c>false</c> —
    /// человек отказался, регистрацию не начинаем. Исполнение свободно или не выбрано — сразу
    /// <c>true</c>, лишних вопросов не задаем.
    /// </summary>
    private async Task<bool> ConfirmDuplicateExecution()
    {
        var org  = OrgCombo.SelectedItem as string ?? "";
        var eskd = (EskdBox.Text ?? "").Trim();

        if (SelectedExecution() is not { } execution) return true;
        if (NumberFor(org, eskd, withExecution: true) is not { } number) return true;
        if (!Designation.ExecutionsOf(_designations, org, eskd, number).Contains(execution)) return true;

        return await new ConfirmDialog(
            $"Обозначение {Designation.FormatExecution(org, eskd, number, execution)} " +
            "уже зарегистрировано в журнале.\n\nЗаписать еще одну строку с тем же обозначением?",
            confirmText: "Все равно записать",
            cancelText:  "Отмена").ShowDialog<bool>(this);
    }

    private RegistrationInput Collect() => new(
        Name:       NameBox.Text       ?? "",
        Org:        OrgCombo.SelectedItem as string ?? "",
        Eskd:       EskdBox.Text        ?? "",
        Fio:        FioBox.Text         ?? "",
        Project:    ProjectBox.Text     ?? "",
        Accounting: AccountingBox.Text  ?? "",
        FirstUse:   FirstUseBox.Text    ?? "",
        Summary:    SummaryBox.Text     ?? "",
        Execution:  SelectedExecution(),
        // Номер называем движку, только когда выбрано исполнение: оно приписывается к
        // ДЕЙСТВУЮЩЕМУ документу, и это тот самый номер, что стоит в окне. Без исполнения номер
        // подбирает движок и в момент записи — так он не уйдет к тому, кто нажал кнопку раньше.
        Number:     SelectedExecution() is null
                        ? null
                        : NumberFor(OrgCombo.SelectedItem as string ?? "",
                                    (EskdBox.Text ?? "").Trim(), withExecution: true),
        // До регистрации доходят только подтвержденные повторы (см. ConfirmDuplicateExecution).
        ConfirmedDuplicate: true);

    // Под следующий документ: ФИО, организация, база и коллекция остаются — меняется документ,
    // а не регистратор.
    //
    // keepDocument — записали исполнение, и документ тот же самый: код ЕСКД, наименование и
    // примечания остаются, меняется только исполнение. Выбирать заново тот же код на каждое из
    // девятнадцати исполнений — работа впустую.
    private void ClearDocumentFields(bool keepDocument)
    {
        if (keepDocument) return;

        _suppressSearch = true;
        EskdBox.Text    = "";
        _suppressSearch = false;

        NameBox.Text       = "";
        ProjectBox.Text    = "";
        AccountingBox.Text = "";
        FirstUseBox.Text   = "";
        SummaryBox.Text    = "";

        // Поле кода опустело — справочник возвращается к классам под следующий документ. Своим
        // номером запрос отменяет все, что было запущено до него.
        _ = RunSearch();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // ── Состояние ─────────────────────────────────────────────────────────────

    private void OnFormChanged(object? sender, TextChangedEventArgs e) => UpdateState();

    // Код организации — тоже часть серии: сменили его, и номер прежней серии больше не наш.
    private void OnTargetChanged(object? sender, SelectionChangedEventArgs e)
    {
        _heldNumber = null;

        UpdateState();
    }

    // Выбор исполнения меняет и номер (при выбранном исполнении он держится за документом), и
    // пометки «занято» в самом списке — пересчитываем все состояние. Перестройку списка за выбор
    // человека не принимаем: иначе она вызвала бы саму себя.
    private void OnExecutionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_rebuildingExecutions) return;

        // «Без исполнения» — способ ОЧИСТИТЬ поле, а не значение в нем: снимаем выбор, и поле
        // снова пустое. Иначе в обозначении стояло бы слово там, где ждут две цифры.
        if (ExecutionCombo.SelectedIndex == 0)
        {
            _rebuildingExecutions = true;
            ExecutionCombo.SelectedIndex = -1;
            _rebuildingExecutions = false;
        }

        UpdateState();
    }

    /// <summary>Выбранное исполнение или <c>null</c> — документ без исполнения. Пустое поле и
    /// первая строка списка дают <c>null</c>, дальше номер исполнения совпадает с номером
    /// строки.</summary>
    private int? SelectedExecution()
    {
        var index = ExecutionCombo.SelectedIndex;

        return index >= Designation.MinExecution && index <= Designation.MaxExecution ? index : null;
    }

    /// <summary>
    /// Номер, который получит документ. <c>null</c> — считать рано (нет серии или коллекции) либо
    /// вся серия занята.
    ///
    /// Без исполнения это первый свободный номер серии — новый документ.
    ///
    /// С исполнением — наоборот, ДЕЙСТВУЮЩИЙ номер: исполнение приписывают к существующему
    /// документу, а не заводят им новый. Берется номер, за которым окно держится (только что
    /// зарегистрированный документ), иначе последний выданный в серии. Не выдано ни одного —
    /// остается первый свободный: исполнение документа, которого в журнале нет, заводить можно,
    /// старые документы туда часто не заносились.
    /// </summary>
    private int? NumberFor(string org, string eskd, bool withExecution)
    {
        if (_shape is null || org.Length == 0 || !EskdCatalog.IsFullCode(eskd)) return null;

        if (withExecution)
            return _heldNumber
                ?? Designation.LastIssued(_designations, org, eskd)
                ?? DocRegistrar.NextNumber(_designations, org, eskd);

        return DocRegistrar.NextNumber(_designations, org, eskd);
    }

    /// <summary>
    /// Перебрать список исполнений: «Без исполнения» и все номера 01–99, у занятых — пометка. Занятые из
    /// списка НЕ убираем: человек должен видеть, что 05 уже выдано, а не гадать, почему список
    /// начинается с 06.
    /// </summary>
    private void RefreshExecutions(string org, string eskd, int? number)
    {
        var taken = number is { } n && _shape is not null
            ? Designation.ExecutionsOf(_designations, org, eskd, n).ToHashSet()
            : [];

        var items = new List<string>(Designation.MaxExecution + 1) { NoExecution };

        for (var e = Designation.MinExecution; e <= Designation.MaxExecution; e++)
            items.Add(taken.Contains(e) ? $"{e:D2} — занято" : $"{e:D2}");

        // Выбор держим по НОМЕРУ строки: он и есть номер исполнения, а текст строки мог
        // поменяться вместе с пометкой. Пустое поле (-1) так и остается пустым.
        var selected = ExecutionCombo.SelectedIndex;

        _rebuildingExecutions = true;
        ExecutionCombo.ItemsSource   = items;
        ExecutionCombo.SelectedIndex = selected < items.Count ? selected : -1;
        _rebuildingExecutions = false;
    }

    /// <summary>Первая строка списка исполнений — ею поле очищают. Значением поля это слово не
    /// становится: выбор первой строки снимает выделение, и поле снова показывает ту же надпись,
    /// но уже серой подписью-подсказкой (<c>PlaceholderText</c> в разметке).</summary>
    private const string NoExecution = "Без исполнения";

    /// <summary>
    /// Пересчитать доступность кнопки и подсказку в статусе. Порядок проверок — порядок
    /// заполнения окна сверху вниз: человек видит ровно тот шаг, на котором стоит.
    /// </summary>
    private void UpdateState()
    {
        if (_busy) return;

        if (ByFile) { UpdateFileState(); return; }

        var org     = OrgCombo.SelectedItem as string ?? "";
        var eskd    = (EskdBox.Text ?? "").Trim();
        var name    = (NameBox.Text ?? "").Trim();
        var project = (ProjectBox.Text ?? "").Trim();

        // Номер и список исполнений — часть обозначения, а не итога проверок: их показываем
        // всегда, на каком бы шаге заполнения человек ни стоял.
        //
        // Пометки «занято» в списке считаются по ДЕЙСТВУЮЩЕМУ номеру всегда, даже когда
        // исполнение еще не выбрано: список для того и открывают, чтобы увидеть, какие
        // исполнения этого документа уже выданы. Считай их по первому свободному номеру —
        // список был бы пуст, и занятое исполнение всплывало бы только после выбора.
        var number = NumberFor(org, eskd, SelectedExecution() is not null);

        NumberBox.Text = number is { } shown ? shown.ToString("D3") : "";
        RefreshExecutions(org, eskd, NumberFor(org, eskd, withExecution: true));

        if (_databases.Count == 0)
        {
            Block("Нет доступной базы данных", red: true);
            return;
        }
        if (org.Length == 0)
        {
            Block("Укажите коды организации: Настройки → Расширения");
            return;
        }
        if (_repo is null)
        {
            Block("База данных недоступна", red: true);
            return;
        }
        if (CollectionCombo.SelectedIndex < 0)
        {
            Block("Выберите коллекцию журнала регистрации");
            return;
        }
        if (_shape is null)
        {
            // Формулировка задания — дословно; вторая половина называет, что именно не сошлось,
            // иначе людям нечего было бы исправлять.
            Block(_schemaProblem is { Length: > 0 } problem
                ? $"Вы выбрали неправильную коллекцию — {problem}"
                : "Вы выбрали неправильную коллекцию", red: true);
            return;
        }
        if (!EskdCatalog.IsFullCode(eskd))
        {
            Block($"Выберите код ЕСКД — {EskdCatalog.CodeLength} цифр");
            return;
        }
        if (name.Length == 0)
        {
            Block("Заполните наименование ЕСКД");
            return;
        }
        if (project.Length == 0)
        {
            Block("Заполните проект");
            return;
        }
        if (number is null)
        {
            Block($"В серии «{Designation.Prefix(org, eskd)}» заняты все номера " +
                  $"{Designation.MinNumber:D3}–{Designation.MaxNumber:D3}", red: true);
            return;
        }

        var execution   = SelectedExecution();
        var designation = execution is { } e
            ? Designation.FormatExecution(org, eskd, number.Value, e)
            : Designation.Format(org, eskd, number.Value);

        // Кнопка работает и на занятом исполнении: это предупреждение, а не запрет, — по нажатию
        // окно спросит подтверждение. Строку с тем же обозначением заводят осознанно.
        RegisterBtn.IsEnabled = true;

        if (execution is { } chosen &&
            Designation.ExecutionsOf(_designations, org, eskd, number.Value).Contains(chosen))
            SetStatus($"Номер занят с таким исполнением: {designation}", "BrushRed");
        else
            SetStatus($"Будет присвоено: {designation}", "BrushAccent");
    }

    private void Block(string message, bool red = false)
    {
        RegisterBtn.IsEnabled = false;
        SetStatus(message, red ? "BrushRed" : "BrushSecondaryText");
    }

    private void SetStatus(string text, string brushKey)
    {
        StatusText.Text = text;
        if (this.TryFindResource(brushKey, out var value) && value is IBrush brush)
            StatusText.Foreground = brush;
    }
}
