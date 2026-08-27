using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Serilog;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>
/// Окно расширения «Регистрация КД» (в основном стиле приложения): ФИО, код организации, подбор
/// кода ЕСКД по справочнику, наименование, база и коллекция журнала, четыре необязательных
/// примечания. По «Зарегистрировать» документ получает обозначение и строку в коллекции.
///
/// Окно НЕ закрывается после записи: документы регистрируют пачками, поэтому ФИО, код
/// организации, база и коллекция остаются, а код ЕСКД, наименование и примечания очищаются под
/// следующий документ. ФИО и коллекция переживают и закрытие окна — лежат в настройках
/// расширения (ключи <c>ext.doc-registry.…</c>, уборка при удалении расширения идет по приставке).
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

    // Номер последнего запущенного подбора: ответ с чужим номером — устаревший и не применяется.
    private int  _searchToken;
    private bool _suppressSearch;
    private bool _busy;

    public DocRegistryWindow()
    {
        InitializeComponent();

        // Справочник распаковывается в фоне, и как только готов — показывается ЦЕЛИКОМ: список
        // открыт до всякого ввода, ввод его фильтрует. До готовности в рамке стоит подпись из
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
    /// Дождаться распаковки справочника и показать его целиком. Пока идет распаковка, в рамке
    /// стоит подпись из разметки — окном можно пользоваться (поля заполняются), список
    /// появляется, как только готов.
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
            ShowSuggestions(new EskdSearch([], 0));
            SuggestEmpty.Text = "Справочник ЕСКД недоступен — код можно ввести цифрами вручную";
            return;
        }

        // Пока распаковывалось, человек мог начать набирать — показываем то, что набрано.
        await RunSearch();
    }

    private void OnEskdTextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateState();
        if (_suppressSearch) return;

        _ = RunSearch();
    }

    /// <summary>
    /// Отфильтровать справочник по тому, что стоит в поле кода. Пустое поле — весь справочник.
    /// Ответ устаревшего запроса (человек набрал дальше) отбрасывается по номеру.
    /// </summary>
    private async Task RunSearch()
    {
        var query = (EskdBox.Text ?? "").Trim();
        var token = ++_searchToken;

        EskdSearch found;
        try
        {
            // В фоне: обход 66 тыс. наименований хоть и быстр, но ввод от него подрагивал бы.
            // Первый вызов вдобавок может застать распаковку справочника.
            found = await Task.Run(() => EskdCatalog.Search(query));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Регистрация КД: подбор по справочнику ЕСКД не выполнен");
            if (token != _searchToken) return;
            ShowSuggestions(new EskdSearch([], 0));
            SuggestEmpty.Text = "Справочник ЕСКД недоступен — код можно ввести цифрами вручную";
            return;
        }

        if (token != _searchToken) return;   // человек набрал дальше — ответ устарел
        ShowSuggestions(found);
    }

    private void OnEskdKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:   MoveSuggestion(+1); e.Handled = true; break;
            case Key.Up:     MoveSuggestion(-1); e.Handled = true; break;

            // Enter фиксирует выбранную строку — так это описано в поле ввода.
            case Key.Enter when SuggestList.SelectedItem is EskdEntry entry:
                Accept(entry);
                e.Handled = true;
                break;

            // Escape снимает ФИЛЬТР, а не прячет список: справочник открыт всегда, и вернуть
            // его целиком — это очистить поле кода.
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
        if (SuggestList.SelectedItem is EskdEntry entry) Accept(entry);
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

        // Список НЕ сбрасываем и фильтр по нему НЕ перезапускаем (см. _suppressSearch выше):
        // выбранная строка остается на виду и выделенной — видно, что именно зафиксировано, —
        // а соседние коды можно тут же пролистать и передумать. Перезапусти мы фильтр, список
        // схлопнулся бы до одной строки.
        SuggestList.SelectedItem = entry;
        if (SuggestList.SelectedIndex >= 0) SuggestList.ScrollIntoView(SuggestList.SelectedIndex);

        SuggestHint.Text = $"Зафиксировано: {entry.Code} — {entry.Name}";

        NameBox.Focus();
        UpdateState();
    }

    private void ShowSuggestions(EskdSearch found)
    {
        SuggestList.ItemsSource   = found.Shown;
        SuggestList.SelectedIndex = found.Shown.Count > 0 ? 0 : -1;
        SuggestList.IsVisible     = found.Shown.Count > 0;
        if (found.Shown.Count > 0) SuggestList.ScrollIntoView(0);   // новый отбор — с начала

        SuggestEmpty.IsVisible = found.Shown.Count == 0;
        SuggestEmpty.Text      = "Ничего не нашлось — проверьте код или попробуйте другое слово";

        SuggestHint.Text = (EskdBox.Text ?? "").Trim().Length == 0
            ? $"Весь классификатор ЕСКД: {found.Total} кодов — начните вводить, чтобы отфильтровать"
            : $"Найдено {found.Total}";
    }

    // ── Регистрация ───────────────────────────────────────────────────────────

    private void OnRegisterClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _repo is null || _shape is null) return;

        var input = Collect();
        string message, brush;

        _busy = true;
        RegisterBtn.IsEnabled = false;
        try
        {
            var done = DocRegistrar.Register(_repo, _shape, input, DateTime.Now);

            // Свежее обозначение — сразу в местный список: предпросмотр следующего номера
            // не пойдет за ним в базу.
            _designations.Add(done.Designation);

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
        EskdBox.Focus();
    }

    private RegistrationInput Collect() => new(
        Name:       NameBox.Text       ?? "",
        Org:        OrgCombo.SelectedItem as string ?? "",
        Eskd:       EskdBox.Text        ?? "",
        Fio:        FioBox.Text         ?? "",
        Topic:      TopicBox.Text       ?? "",
        Accounting: AccountingBox.Text  ?? "",
        FirstUse:   FirstUseBox.Text    ?? "",
        Summary:    SummaryBox.Text     ?? "");

    // Под следующий документ: ФИО, организация, база и коллекция остаются — меняется документ,
    // а не регистратор.
    private void ClearDocumentFields()
    {
        _suppressSearch = true;
        EskdBox.Text    = "";
        _suppressSearch = false;

        NameBox.Text       = "";
        TopicBox.Text      = "";
        AccountingBox.Text = "";
        FirstUseBox.Text   = "";
        SummaryBox.Text    = "";

        // Фильтр снят — возвращаем весь справочник под следующий документ. Своим номером
        // запрос отменяет все, что было запущено до него.
        _ = RunSearch();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // ── Состояние ─────────────────────────────────────────────────────────────

    private void OnFormChanged(object? sender, TextChangedEventArgs e) => UpdateState();

    private void OnTargetChanged(object? sender, SelectionChangedEventArgs e) => UpdateState();

    /// <summary>
    /// Пересчитать доступность кнопки и подсказку в статусе. Порядок проверок — порядок
    /// заполнения окна сверху вниз: человек видит ровно тот шаг, на котором стоит.
    /// </summary>
    private void UpdateState()
    {
        if (_busy) return;

        var org  = OrgCombo.SelectedItem as string ?? "";
        var eskd = (EskdBox.Text ?? "").Trim();
        var name = (NameBox.Text ?? "").Trim();

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
