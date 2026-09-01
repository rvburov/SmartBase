using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Serilog;
using SmartBase.Controls.Dialogs;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>
/// Окно расширения «Регистрация КД» (в основном стиле приложения): ФИО, код организации, подбор
/// кода ЕСКД по справочнику, наименование, база и коллекция журнала, обязательный проект и три
/// необязательных примечания. По «Зарегистрировать» документ получает обозначение и строку в
/// коллекции.
///
/// Окно НЕ закрывается после записи: документы регистрируют пачками, поэтому ФИО, код
/// организации, база и коллекция остаются, а код ЕСКД, наименование, проект и примечания
/// очищаются под следующий документ. ФИО и коллекция переживают и закрытие окна — лежат в
/// настройках расширения (ключи <c>ext.doc-registry.…</c>, уборка при удалении расширения идет
/// по приставке).
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

    /// <summary>Обозначение → наименование записи: подсказке о том, к какому документу идет
    /// исполнение. Читается вместе с обозначениями, один раз на выбор коллекции.</summary>
    private Dictionary<string, string> _names = [];

    // Номер последнего запущенного подбора: ответ с чужим номером — устаревший и не применяется.
    private int  _searchToken;
    private bool _suppressSearch;
    private bool _busy;

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
        var orgs = DocRegistryExtension.ParseOrgCodes(
            AppSettings.LoadExtensionSetting(DocRegistryExtension.ExtensionId,
                                             DocRegistryExtension.OrgCodesKey));
        OrgCombo.ItemsSource   = orgs;
        OrgCombo.IsEnabled     = orgs.Count > 0;
        OrgCombo.SelectedIndex = orgs.Count > 0 ? 0 : -1;

        // ФИО — тоже настройка расширения: у машины оно одно и меняется редко. Окно подставляет
        // сохраненное и после регистрации пишет обратно то, что стоит в поле, — значение общее
        // с экраном настроек.
        FioBox.Text = AppSettings.LoadExtensionSetting(DocRegistryExtension.ExtensionId,
                                                       DocRegistryExtension.FioKey);

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
        _names         = [];

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
                    _names        = DocRegistrar.NamesByDesignation(_repo, shape);
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
        SuggestList.IsVisible   = rows.Count > 0;

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

        SuggestEmpty.IsVisible = rows.Count == 0;
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

        // Занятое исполнение — не отказ, а вопрос: бывает, что документ в журнал занесли
        // задним числом или строку завели ошибочно, и решать это вправе только человек.
        // Подтверждение спрашиваем ЗДЕСЬ, а не в движке: движок без подтверждения откажет.
        if (!await ConfirmDuplicateExecution()) return;

        var input = Collect();
        string message, brush;

        _busy = true;
        RegisterBtn.IsEnabled = false;
        try
        {
            var done = DocRegistrar.Register(_repo, _shape, input, DateTime.Now);

            // Свежее обозначение — сразу в местный список: предпросмотр следующего номера
            // не пойдет за ним в базу. В подсказку по исполнениям — тоже: следующее исполнение
            // того же документа заводят прямо сейчас, и она должна быть верной.
            _designations.Add(done.Designation);
            _names.TryAdd(done.Designation, input.Name.Trim());

            AppSettings.SaveExtensionSetting(DocRegistryExtension.ExtensionId,
                                             DocRegistryExtension.FioKey, input.Fio.Trim());

            Log.Information("Регистрация КД: {Designation} — коллекция «{Collection}»",
                            done.Designation, _shape.CollectionName);

            ClearDocumentFields();
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

        // Сперва общее состояние (кнопка и подсказка по опустевшим полям), затем итог
        // регистрации — он важнее подсказки и должен остаться на виду.
        UpdateState();
        SetStatus(message, brush);

        // Курсор — в то поле, с которого начинается следующий документ. В режиме исполнения это
        // не код ЕСКД (он остался прежним), а само исполнение.
        if (ExecutionCheck.IsChecked == true) ExecutionBox.Focus();
        else                                  EskdBox.Focus();
    }

    /// <summary>
    /// Спросить подтверждение, если выбранное исполнение уже зарегистрировано. <c>false</c> —
    /// человек отказался, регистрацию не начинаем. Не режим исполнения или исполнение свободно —
    /// сразу <c>true</c>, лишних вопросов не задаем.
    /// </summary>
    private async Task<bool> ConfirmDuplicateExecution()
    {
        if (!TakenExecutionChosen(out var designation)) return true;

        return await new ConfirmDialog(
            $"Обозначение {designation} уже зарегистрировано в журнале.\n\n" +
            "Записать еще одну строку с тем же обозначением?",
            confirmText: "Все равно записать",
            cancelText:  "Отмена").ShowDialog<bool>(this);
    }

    /// <summary>Выбрано ли сейчас исполнение, которое уже занято, и какое обозначение выйдет.
    /// Один расчет на подсказку в статусе и на вопрос по кнопке — чтобы они не разошлись.</summary>
    private bool TakenExecutionChosen(out string designation)
    {
        designation = "";

        if (ExecutionCheck.IsChecked != true) return false;

        var org  = OrgCombo.SelectedItem as string ?? "";
        var eskd = (EskdBox.Text ?? "").Trim();

        if (!TryBaseNumber(out var number) || !TryExecution(out var execution)) return false;

        designation = Designation.FormatExecution(org, eskd, number, execution);
        return Designation.ExecutionsOf(_designations, org, eskd, number).Contains(execution);
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
        ForExecution: CollectExecution());

    /// <summary>Заявка на исполнение или <c>null</c> — обычная регистрация нового номера.
    /// Подтверждение проставлено заранее: до этого места доходят только подтвержденные повторы
    /// (см. <see cref="ConfirmDuplicateExecution"/>).</summary>
    private ExecutionRequest? CollectExecution()
    {
        if (ExecutionCheck.IsChecked != true) return null;
        if (!TryBaseNumber(out var number)) return null;

        return AutoExecutionCheck.IsChecked == true
            ? new ExecutionRequest(number)
            : new ExecutionRequest(number,
                                   TryExecution(out var execution) ? execution : null,
                                   Confirmed: true);
    }

    // Под следующий документ: ФИО, организация, база и коллекция остаются — меняется документ,
    // а не регистратор.
    //
    // В режиме исполнения серию и основной номер НЕ трогаем: исполнения одного документа заводят
    // подряд, и заново выбирать тот же код ЕСКД на каждое из них — работа впустую.
    private void ClearDocumentFields()
    {
        if (ExecutionCheck.IsChecked == true)
        {
            ExecutionBox.Text = "";
            return;
        }

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

    private void OnTargetChanged(object? sender, SelectionChangedEventArgs e) => UpdateState();

    // Тумблер режима: поля исполнения показываются только когда они нужны, иначе окно выглядело
    // бы так, будто без них регистрация неполная.
    private void OnExecutionModeChanged(object? sender, RoutedEventArgs e)
    {
        var on = ExecutionCheck.IsChecked == true;

        ExecutionFields.IsVisible = on;
        ExecutionHint.IsVisible   = on;

        UpdateState();
    }

    // «Подобрать сам» — поле исполнения не просто игнорируется, а гаснет: иначе в нем оставалось
    // бы число, которое ни на что не влияет, и человек считал бы, что регистрирует именно его.
    private void OnAutoExecutionChanged(object? sender, RoutedEventArgs e)
    {
        ExecutionBox.IsEnabled = AutoExecutionCheck.IsChecked != true;

        UpdateState();
    }

    /// <summary>Основной номер из поля — три цифры в пределах серии.</summary>
    private bool TryBaseNumber(out int number) =>
        TryDigits(BaseNumberBox.Text, 3, Designation.MinNumber, Designation.MaxNumber, out number);

    /// <summary>Номер исполнения: из поля или подобранный, смотря по тумблеру «подобрать сам».
    /// <c>false</c> — поле заполнено неверно или свободных исполнений не осталось.</summary>
    private bool TryExecution(out int execution)
    {
        execution = 0;

        if (AutoExecutionCheck.IsChecked != true)
            return TryDigits(ExecutionBox.Text, 2,
                             Designation.MinExecution, Designation.MaxExecution, out execution);

        var org  = OrgCombo.SelectedItem as string ?? "";
        var eskd = (EskdBox.Text ?? "").Trim();

        if (!TryBaseNumber(out var number)) return false;

        if (DocRegistrar.NextExecution(_designations, org, eskd, number) is not { } picked) return false;

        execution = picked;
        return true;
    }

    // Ровно столько цифр, сколько положено, и в своем диапазоне. «12» вместо «012» не проходит
    // по той же причине, по какой не проходит в журнале: угадывать за человека нельзя.
    private static bool TryDigits(string? text, int length, int min, int max, out int value)
    {
        value = 0;

        var digits = (text ?? "").Trim();
        if (digits.Length != length || !digits.All(char.IsAsciiDigit)) return false;

        value = int.Parse(digits, CultureInfo.InvariantCulture);
        return value >= min && value <= max;
    }

    /// <summary>
    /// Состояние в режиме исполнения. Отличий от обычного два: номер серии не выдается (его
    /// указывает человек), а занятое исполнение не запрещает запись — по кнопке о нем спросят.
    /// </summary>
    private void UpdateExecutionState(string org, string eskd)
    {
        if (!TryBaseNumber(out var number))
        {
            ExecutionHint.Text = "";
            Block($"Введите основной номер — три цифры, {Designation.MinNumber:D3}–{Designation.MaxNumber:D3}");
            return;
        }

        ShowExecutionHint(org, eskd, number);

        if (AutoExecutionCheck.IsChecked == true &&
            DocRegistrar.NextExecution(_designations, org, eskd, number) is null)
        {
            Block($"У документа {Designation.Format(org, eskd, number)} заняты все исполнения " +
                  $"{Designation.MinExecution:D2}–{Designation.MaxExecution:D2}", red: true);
            return;
        }
        if (!TryExecution(out var execution))
        {
            Block($"Введите исполнение — две цифры, {Designation.MinExecution:D2}–{Designation.MaxExecution:D2}");
            return;
        }

        var designation = Designation.FormatExecution(org, eskd, number, execution);

        // Кнопка остается ВКЛЮЧЕННОЙ и на занятом исполнении: это предупреждение, а не запрет,
        // и подтверждение спрашивается по нажатию.
        RegisterBtn.IsEnabled = true;

        if (Designation.ExecutionsOf(_designations, org, eskd, number).Contains(execution))
            SetStatus($"{designation} уже зарегистрировано — по кнопке спросим подтверждение", "BrushRed");
        else
            SetStatus($"Будет присвоено: {designation}", "BrushAccent");
    }

    /// <summary>
    /// Строка под полями исполнения: к какому документу оно приписывается и что у него уже занято.
    /// Наименование основного документа важнее всего остального: три цифры сами по себе ни о чем
    /// не говорят, а промах виден сразу, если рядом стоит чужое наименование.
    /// </summary>
    private void ShowExecutionHint(string org, string eskd, int number)
    {
        var baseDesignation = Designation.Format(org, eskd, number);
        var executions      = Designation.ExecutionsOf(_designations, org, eskd, number);

        var head = Designation.NumberTaken(_designations, org, eskd, number)
            ? _names.TryGetValue(baseDesignation, out var title) && title.Length > 0
                ? $"{baseDesignation} — «{title}»"
                : $"{baseDesignation} — есть в журнале"
            : $"{baseDesignation} — в журнале НЕТ такого документа";

        var tail = executions.Count == 0
            ? "исполнений пока нет"
            : $"заняты исполнения {string.Join(", ", executions.Select(e => e.ToString("D2")))}";

        ExecutionHint.Text = $"{head}; {tail}";
    }

    /// <summary>
    /// Пересчитать доступность кнопки и подсказку в статусе. Порядок проверок — порядок
    /// заполнения окна сверху вниз: человек видит ровно тот шаг, на котором стоит.
    /// </summary>
    private void UpdateState()
    {
        if (_busy) return;

        var org     = OrgCombo.SelectedItem as string ?? "";
        var eskd    = (EskdBox.Text ?? "").Trim();
        var name    = (NameBox.Text ?? "").Trim();
        var project = (ProjectBox.Text ?? "").Trim();

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
        if (ExecutionCheck.IsChecked == true)
        {
            UpdateExecutionState(org, eskd);
            return;
        }

        var number = DocRegistrar.NextNumber(_designations, org, eskd);
        if (number is null)
        {
            Block($"В серии «{Designation.Prefix(org, eskd)}» заняты все номера " +
                  $"{Designation.MinNumber:D3}–{Designation.MaxNumber:D3}", red: true);
            return;
        }

        RegisterBtn.IsEnabled = true;
        SetStatus($"Будет присвоено: {Designation.Format(org, eskd, number.Value)}", "BrushAccent");
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
