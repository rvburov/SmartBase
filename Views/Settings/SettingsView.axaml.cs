using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SmartBase.Controls.Dialogs;
using SmartBase.Controls.Widgets;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Extensions;
using SmartBase.Integrations;
using SmartBase.Settings;
using SmartBase.Utils;

namespace SmartBase.Views.Settings;

public partial class SettingsView : UserControl
{
    private DatabaseRegistry? _registry;
    // Статус БД в фоновой очереди монтирования (текст + признак ошибки) или null,
    // если базы в очереди нет; поставляет MainWindow из MountQueue.
    private Func<int, (string Text, bool Failed)?>? _queueStatus;
    // Признак «БД сейчас смонтирована» (дает MainWindow по рабочему пространству).
    private Func<int, bool>?  _isMounted;

    // Ползунок удерживается мышью: применение масштаба/шрифта отложено до отпускания
    // (живое применение перекладывает окно, и ползунок «уезжает» из-под курсора).
    private bool _scaleDragging;
    private bool _fontDragging;

    // Реестр изменен — MainWindow монтирует/размонтирует БД и обновляет сайдбар.
    public event EventHandler<DatabaseRefEntity>? DatabaseAdded;
    public event EventHandler<int>?               DatabaseRemoved;
    // Попытка переподключить недоступную БД (по DbId).
    public event EventHandler<int>?               DatabaseReconnectRequested;
    /// <summary>«Сжать файл» у смонтированной SQLite-базы: MainWindow отцепляет базу и ведет
    /// VACUUM через очередь монтирования (см. MountQueue.RunMaintenance).</summary>
    public event EventHandler<int>?               DatabaseCompactRequested;
    /// <summary>База переименована в окне настроек (имя уже записано в реестр): MainWindow
    /// обновляет имя смонтированной БД на месте и перечитывает сайдбары — без ремаунта.</summary>
    public event EventHandler<(int Id, string Name)>? DatabaseRenamed;
    // Тумблер «Общее → Уведомления» переключен — MainWindow сообщает службе уведомлений.
    public event EventHandler<bool>?              NotificationsEnabledChanged;
    // Тумблеры уведомлений по задачам — туда же, каждый сам по себе.
    public event EventHandler<bool>?              TaskAssignedNotificationsChanged;
    public event EventHandler<bool>?              TaskStatusNotificationsChanged;
    // Тумблер «Общее → Фоновый режим» переключен — MainWindow показывает/прячет значок в трее.
    public event EventHandler<bool>?              BackgroundEnabledChanged;
    // Выбрана (или очищена) папка хранилища заметок — MainWindow передает путь дереву и разделу.
    public event EventHandler<string>?            NotesVaultChanged;
    // «Оформление → Отображение …»: набор скрытых в области (ViewCatalog.Scope*) сменился и уже
    // сохранен. MainWindow раздает его тем, кто показывает эту область: без события панель
    // разделов узнавала о скрытом разделе только при следующем запуске.
    public event EventHandler<string>?            ViewVisibilityChanged;

    // Тумблеры раздела «Общее» инициализированы — до этого IsCheckedChanged не обрабатываем
    // (иначе присвоение IsChecked на старте записало бы автозапуск в ОС на каждом запуске).
    private bool _generalLoaded;

    public SettingsView()
    {
        InitializeComponent();
        ApplyStartupUpdateStatus();

        // ── О программе ──────────────────────────────────────────────────────
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version is not null)
        {
            var text = $"Версия {version.Major}.{version.Minor}.{version.Build}";
            if (BuildDate() is { } built) text += $" · сборка {built:dd.MM.yyyy}";
            // Особая сборка называет себя здесь. Без этого через полгода никто не скажет, какая
            // из них стоит на машине, а отличаются они тем, о чем спрашивает служба безопасности
            // (docs/ROADMAP-API.md, 10.16.1).
            if (BuildFlavour() is { Length: > 0 } flavour) text += " · " + flavour;
            VersionText.Text = text;
        }
        DataPathText.Text   = AppSettings.AppDataDir;
        SystemInfoText.Text = $"{RuntimeInformation.OSDescription.Trim()} · {RuntimeInformation.FrameworkDescription}";
        UpdatePathBox.Text  = AppSettings.LoadUpdatePath();
        AutoUpdateToggle.IsChecked = AppSettings.LoadAutoUpdate();

        // ── Заметки ──────────────────────────────────────────────────────────
        NotesVaultBox.Text = AppSettings.LoadNotesVault();
        RefreshNotesVaultStatus();

        // ── Общее ────────────────────────────────────────────────────────────
        NotificationsToggle.IsChecked = AppSettings.LoadNotificationsEnabled();
        TaskAssignedToggle.IsChecked  = AppSettings.LoadNotifyTaskAssigned();
        TaskStatusToggle.IsChecked    = AppSettings.LoadNotifyTaskStatus();
        AutostartToggle.IsEnabled     = Autostart.IsSupported;   // в dev-режиме (без бандла/exe) недоступно
        // Состояние берем у САМОЙ системы, а не из app.db: запись могли снести мимо приложения,
        // и тумблер показывал бы автозапуск, которого нет. Вне установки читать нечего — там
        // остается сохраненная настройка (тумблер все равно неактивен).
        AutostartToggle.IsChecked     = Autostart.IsSupported ? Autostart.IsEnabled()
                                                             : AppSettings.LoadAutostartEnabled();
        RefreshAutostartHint();
        BackgroundToggle.IsChecked    = AppSettings.LoadBackgroundEnabled();
        _generalLoaded = true;

        // Слайдер размера шрифта отражает текущую дельту (стандарт = 0, диапазон -3…+3).
        FontSizeSlider.Value = FontScale.Delta;

        // Слайдер масштаба отражает текущий уровень в процентах (стандарт = 100, диапазон 70…130).
        ScaleSlider.Value = Scaling.Current * 100;

        // Применение — только после отпускания ползунка. Tunnel-обработчики видят
        // нажатие/отпускание даже когда Thumb/трек помечают событие обработанным;
        // Thumb.DragCompleted (всплывает) страхует отпускание вне окна/потерю захвата.
        ScaleSlider.AddHandler(PointerPressedEvent,  (_, _) => _scaleDragging = true, RoutingStrategies.Tunnel);
        ScaleSlider.AddHandler(PointerReleasedEvent, (_, _) => EndScaleDrag(),        RoutingStrategies.Tunnel);
        ScaleSlider.AddHandler(Thumb.DragCompletedEvent, (_, _) => EndScaleDrag());
        FontSizeSlider.AddHandler(PointerPressedEvent,  (_, _) => _fontDragging = true, RoutingStrategies.Tunnel);
        FontSizeSlider.AddHandler(PointerReleasedEvent, (_, _) => EndFontDrag(),        RoutingStrategies.Tunnel);
        FontSizeSlider.AddHandler(Thumb.DragCompletedEvent, (_, _) => EndFontDrag());

        // «Оформление»: обе группы строк (что открывается по умолчанию и что показывать).
        InitAppearance();

        // «Расширения»: галочки видимости. Подписка один раз здесь, наполнение — при каждом
        // показе раздела (BuildExtensionsPanel), иначе обработчик копился бы на каждый заход.
        ExtensionsVisibility.HiddenChanged += OnExtensionsVisibilityChanged;

        // Меню «Добавить базу данных» живет в навбаре (видно только в разделе БД,
        // см. ShowCategory) — пункты ведут на те же обработчики, что прежние кнопки.
        HeaderBar.CreateSqliteRequested    += (_, _) => OnCreateDatabaseClick(this, new RoutedEventArgs());
        HeaderBar.ConnectSqliteRequested   += (_, _) => OnConnectDatabaseClick(this, new RoutedEventArgs());
        HeaderBar.ConnectPostgresRequested += (_, _) => OnConnectPostgresClick(this, new RoutedEventArgs());
        HeaderBar.CreatePostgresRequested  += (_, _) => OnCreatePostgresClick(this, new RoutedEventArgs());
        HeaderBar.ExportLogsRequested      += (_, _) => _ = ExportLogsToCsv();

        // Переключатель вкладок «Интеграций» — там же, в навбаре (виден только в этом разделе).
        HeaderBar.IntegrationTabRequested  += (_, key) => ShowIntegrationTab(key);

        // Поиск по сочетаниям клавиш — тоже в навбаре; каждая правка строки пересобирает список.
        HeaderBar.ShortcutSearchChanged    += (_, text) => OnShortcutSearchChanged(text);
    }

    // ── Оформление: что открывается по умолчанию и что вообще показывать ─────
    //
    // Обе группы строк ведет один код по ViewCatalog: слева выпадающий список ТОЛЬКО из
    // видимых пунктов, справа галочки видимости. Ни разметка, ни этот файл не знают ни
    // одного вида по имени — появился вид в каталоге, и обе строки подхватят его сами.
    //
    // Связь между строками: сняли галочку с пункта, который стоял «по умолчанию», —
    // умолчание переезжает на первый видимый и сразу сохраняется. Иначе настройка
    // указывала бы на вид, которого человек больше не видит.
    //
    // Читает и пишет каждый раздел своей парой из AppSettings: ключи у них исторически
    // разные (у календаря — номер вида, у остальных — строка), поэтому перевод «ключ
    // каталога ↔ хранимое значение» задается здесь и больше нигде не повторяется.
    private sealed record AppearanceScope(
        string            Scope,
        ComboBox          DefaultCombo,
        CheckListDropdown Visibility,
        Func<string>      LoadKey,
        Action<string>    SaveKey);

    private List<AppearanceScope> _appearance = [];

    // Гасит обработчики, пока список наполняется кодом: смена ItemsSource дергает
    // SelectionChanged, и без флага перестройка перезаписывала бы настройку.
    private bool _appearanceBuilding;

    private void InitAppearance()
    {
        _appearance =
        [
            new(ViewCatalog.ScopeNavigation, StartupModuleCombo, NavigationVisibility,
                AppSettings.LoadStartupModule,
                AppSettings.SaveStartupModule),

            new(ViewCatalog.ScopeCalendar, CalendarViewCombo, CalendarVisibility,
                () => ViewCatalog.KeyAt(ViewCatalog.ScopeCalendar, AppSettings.LoadCalendarView()),
                key => AppSettings.SaveCalendarView(
                           Math.Max(0, ViewCatalog.IndexOf(ViewCatalog.ScopeCalendar, key)))),

            new(ViewCatalog.ScopeTasks, TasksViewCombo, TasksVisibility,
                AppSettings.LoadTasksView,
                AppSettings.SaveTasksView),

            // Библиотека — на общих основаниях, как задачи: вид пока один, но набор настроек у
            // разделов одинаковый, и отсутствие блока читалось бы как недоделка, а не как
            // решение. Переключателя вида в навбаре у нее при этом нет — там выпадашка на один
            // пункт стояла бы в рабочей полосе и ничем не управляла.
            new(ViewCatalog.ScopeLibrary, LibraryViewCombo, LibraryVisibility,
                AppSettings.LoadLibraryView,
                AppSettings.SaveLibraryView),

            new(ViewCatalog.ScopeConstructor, ConstructorViewCombo, ConstructorVisibility,
                AppSettings.LoadConstructorView,
                AppSettings.SaveConstructorView),
        ];

        foreach (var scope in _appearance)
        {
            var captured = scope;
            captured.DefaultCombo.SelectionChanged += (_, _) => OnAppearanceDefaultChanged(captured);
            captured.Visibility.HiddenChanged      += (_, hidden) => OnAppearanceVisibilityChanged(captured, hidden);
            RebuildAppearanceScope(captured);
        }
    }

    private void RebuildAppearanceScope(AppearanceScope scope)
    {
        _appearanceBuilding = true;

        scope.Visibility.SetItems(ViewCatalog.All(scope.Scope),
                                  AppSettings.LoadHiddenViews(scope.Scope));

        var visible = ViewCatalog.Visible(scope.Scope);
        scope.DefaultCombo.ItemsSource           = visible;
        scope.DefaultCombo.DisplayMemberBinding  = new Binding(nameof(ViewItem.Label));

        // Сохраненного пункта может не быть среди видимых только в переходный момент
        // (настройку правили руками) — тогда показываем первый видимый.
        var current = scope.LoadKey();
        var index   = visible.ToList().FindIndex(v => v.Key == current);
        scope.DefaultCombo.SelectedIndex = index >= 0 ? index : 0;

        _appearanceBuilding = false;
    }

    private void OnAppearanceDefaultChanged(AppearanceScope scope)
    {
        if (_appearanceBuilding) return;
        if (scope.DefaultCombo.SelectedItem is ViewItem item) scope.SaveKey(item.Key);
    }

    private void OnAppearanceVisibilityChanged(AppearanceScope scope, IReadOnlyList<string> hidden)
    {
        AppSettings.SaveHiddenViews(scope.Scope, hidden);

        if (!ViewCatalog.IsVisible(scope.Scope, scope.LoadKey()) &&
            ViewCatalog.FirstVisible(scope.Scope) is { } first)
            scope.SaveKey(first.Key);

        RebuildAppearanceScope(scope);
        ViewVisibilityChanged?.Invoke(this, scope.Scope);
    }

    // ── Общее ────────────────────────────────────────────────────────────────
    //
    // Тумблер «Уведомления»: сохраняем сразу и сообщаем службе (MainWindow), чтобы
    // включение/выключение действовало на лету — без перезапуска.
    private void OnNotificationsToggled(object? sender, RoutedEventArgs e)
    {
        var on = NotificationsToggle.IsChecked == true;
        AppSettings.SaveNotificationsEnabled(on);
        NotificationsEnabledChanged?.Invoke(this, on);
    }

    // Уведомления по задачам — тем же путем: сохранить и сообщить службе. Включение показывает
    // только то, что случится дальше: накопленное за время «выключено» служба не вываливает.
    private void OnTaskAssignedToggled(object? sender, RoutedEventArgs e)
    {
        var on = TaskAssignedToggle.IsChecked == true;
        AppSettings.SaveNotifyTaskAssigned(on);
        TaskAssignedNotificationsChanged?.Invoke(this, on);
    }

    private void OnTaskStatusToggled(object? sender, RoutedEventArgs e)
    {
        var on = TaskStatusToggle.IsChecked == true;
        AppSettings.SaveNotifyTaskStatus(on);
        TaskStatusNotificationsChanged?.Invoke(this, on);
    }

    // Автозапуск: сначала запись в ОС, и только если она легла — сохраняем настройку. Запись не
    // зависит от фонового режима: при входе в систему окно открывается в любом случае (Utils/Autostart).
    //
    // Раньше сбой записи проглатывался молча: настройка сохранялась, тумблер оставался включенным,
    // а автозапуска не было — и понять это снаружи было нечем. Теперь отказ (нет прав, политика
    // домена, откат антивирусом) виден: тумблер возвращается в фактическое состояние + окно с причиной.
    private void OnAutostartToggled(object? sender, RoutedEventArgs e)
    {
        if (!_generalLoaded) return;
        var on = AutostartToggle.IsChecked == true;

        if (!Autostart.Set(on))
        {
            var actual = Autostart.IsEnabled();
            _generalLoaded = false;              // возврат тумблера не должен снова звать обработчик
            AutostartToggle.IsChecked = actual;
            _generalLoaded = true;
            AppSettings.SaveAutostartEnabled(actual);
            if (VisualRoot is Window win)
                _ = ConfirmDialog.Info(
                    on ? "Не удалось включить автозапуск: система отклонила запись. Такое бывает из-за политик " +
                         "организации или защитных программ — проверьте автозагрузку в параметрах системы."
                       : "Не удалось выключить автозапуск: система отклонила изменение записи. " +
                         "Убрать ее можно в параметрах системы, раздел «Автозагрузка».")
                    .ShowDialog(win);
            return;
        }

        AppSettings.SaveAutostartEnabled(on);
        RefreshAutostartHint();
    }

    // Запись автозагрузки может быть отключена средствами системы (Диспетчер задач Windows).
    // Тогда тумблер честно показывает «включено» — запись на месте, — но при входе в систему
    // программа не стартует. Проверяем и после переключения: снятие и повторное включение
    // записи отметку Диспетчера не сбрасывает.
    private void RefreshAutostartHint()
    {
        // Тумблер серый — сказать почему: отладочная сборка или копия не из папки установки.
        // Раньше он просто гас, и это принимали за поломку после обновления.
        if (!Autostart.IsSupported)
        {
            AutostartHint.IsVisible = true;
            AutostartHint.Text      = Autostart.UnsupportedReason();
            return;
        }
        var disabled = Autostart.DisabledInSystemUi();
        AutostartHint.IsVisible = disabled;
        if (!disabled) return;
        AutostartHint.Text = "Запись автозапуска отключена в системе: Диспетчер задач → «Автозагрузка». " +
                             "Пока она там выключена, программа при входе в систему не запустится.";
        if (this.TryFindResource("BrushRed", out var res) && res is Avalonia.Media.IBrush brush)
            AutostartHint.Foreground = brush;
    }

    // Фоновый режим: сохраняем + сообщаем MainWindow (значок в трее). Автозапуск не трогаем —
    // тумблеры независимы: фон отвечает только за «крестик» окна и значок.
    private void OnBackgroundToggled(object? sender, RoutedEventArgs e)
    {
        if (!_generalLoaded) return;
        var on = BackgroundToggle.IsChecked == true;
        AppSettings.SaveBackgroundEnabled(on);
        BackgroundEnabledChanged?.Invoke(this, on);
    }

    // ── Расширения ───────────────────────────────────────────────────────────
    //
    // Раздел собирается ЦЕЛИКОМ из реестра: по одной строке на расширение, объявившее
    // настройки в IExtension.Settings. Ни одного расширения по имени здесь нет — убрали
    // расширение из ExtensionRegistry, и его блок исчез сам, без правок этого экрана.
    //
    // Скрытые расширения в список настроек не попадают: скрыли — убрали с глаз целиком,
    // и в разделе, и здесь. Единственное место, где скрытое остается видимым, — строка
    // галочек выше: иначе вернуть его было бы нечем.
    //
    // Перестраиваем при каждом показе: значение могло смениться мимо настроек — «Обзор»
    // в окне расширения пишет в те же ключи.
    private void BuildExtensionsPanel()
    {
        BuildExtensionsVisibility();
        BuildExtensionsSettingsList();
    }

    private void BuildExtensionsSettingsList()
    {
        ExtensionsSettingsList.Children.Clear();

        var withSettings = ExtensionRegistry.All
            .Where(e => e.Settings.Count > 0)
            .Where(e => ViewCatalog.IsVisible(ViewCatalog.ScopeExtensions, e.Id))
            .ToList();
        if (withSettings.Count == 0)
        {
            ExtensionsSettingsList.Children.Add(new Border
            {
                Classes = { "settingRow" },
                Child = new TextBlock
                {
                    Classes = { "desc" },
                    Text = "Ни одно показанное расширение не требует настройки.",
                },
            });
            return;
        }

        foreach (var extension in withSettings)
        {
            var rows = new StackPanel { Spacing = 10 };
            rows.Children.Add(new TextBlock { Classes = { "title" }, Text = extension.Name });

            foreach (var setting in extension.Settings)
                rows.Children.Add(BuildSettingField(extension, setting));

            ExtensionsSettingsList.Children.Add(new Border { Classes = { "settingRow" }, Child = rows });
            ExtensionsSettingsList.Children.Add(new Border { Classes = { "separator", "h" } });
        }
    }

    // Галочки «какие расширения показывать». Пункты — не список в разметке, а сам реестр
    // (ViewCatalog.ScopeExtensions): удалили расширение — пункт исчез, а его Id уберется из
    // «скрытых» при следующем запуске (AppSettings.PruneHiddenViews в App.axaml.cs).
    //
    // Строка живет здесь, а не в «Оформлении» рядом с прочими «Отображение раздела …»:
    // там перечислены ВИДЫ разделов, а тут — состав самого раздела расширений. Реестр пуст —
    // прятать нечего, и блок скрывается целиком.
    private void BuildExtensionsVisibility()
    {
        var items = ViewCatalog.All(ViewCatalog.ScopeExtensions);
        ExtensionsVisibilityBlock.IsVisible = items.Count > 0;
        if (items.Count == 0) return;

        ExtensionsVisibility.SetItems(items, AppSettings.LoadHiddenViews(ViewCatalog.ScopeExtensions));
    }

    // Набор скрытых поменяли: сохраняем и тут же убираем (или возвращаем) блоки настроек
    // ниже — они на этом же экране, и ждать перезахода незачем. Сам список галочек НЕ
    // перестраиваем: попап в этот момент открыт под курсором, а SetItems пересоздал бы его
    // строки прямо под нажатием.
    //
    // Сайдбар раздела расширений пересобирается при заходе в него (MainWindow.ShowModule):
    // пока человек в настройках, тот список скрыт, и события наружу не нужно.
    private void OnExtensionsVisibilityChanged(object? sender, IReadOnlyList<string> hidden)
    {
        AppSettings.SaveHiddenViews(ViewCatalog.ScopeExtensions, hidden);
        BuildExtensionsSettingsList();
    }

    // Поле одной настройки: подпись, под ней строка ввода. Значение сохраняется при потере
    // фокуса — тот же прием, что у пути обновления.
    private StackPanel BuildSettingField(IExtension extension, ExtensionSetting setting)
    {
        var block = new StackPanel { Spacing = 6 };
        block.Children.Add(new TextBlock { Classes = { "desc" }, Text = setting.Label });

        var box = new TextBox
        {
            Classes   = { "extSetting" },
            Watermark = setting.Watermark,
            Text      = AppSettings.LoadExtensionSetting(extension.Id, setting.Key),
        };
        box.LostFocus += (_, _) =>
            AppSettings.SaveExtensionSetting(extension.Id, setting.Key, box.Text ?? "");

        block.Children.Add(box);

        if (setting.Button is { } button)
            block.Children.Add(BuildSettingButton(extension, setting, button, box));

        return block;
    }

    // Кнопка под полем настройки: что она делает и что говорит человеку, решает расширение.
    // Оболочка дает ей значение поля и отправку путем ответов сценариев (ActionReplies) — сама
    // кнопка ни сценариев, ни запросов не знает.
    private Button BuildSettingButton(IExtension extension, ExtensionSetting setting,
                                      ExtensionSettingButton button, TextBox box)
    {
        // Текстовые кнопки настроек — общий стиль «dialog»; «auto» снимает фиксированную ширину.
        var run = new Button
        {
            Classes             = { "dialog", "auto" },
            Content             = button.Text,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        };

        run.Click += async (_, _) =>
        {
            if (VisualRoot is not Window owner) return;

            // Значение — то, что сейчас в поле, и сохраняется оно до нажатия: человек мог дописать
            // строку и сразу нажать кнопку, а потеря фокуса приходит не раньше щелчка.
            var value = box.Text ?? "";
            AppSettings.SaveExtensionSetting(extension.Id, setting.Key, value);

            run.IsEnabled = false;
            try
            {
                var replies = _registry is { } registry ? new ActionReplies(registry.Integrations) : null;
                await button.Run(new ExtensionSettingRun(owner, value, replies));
            }
            catch (Exception ex)
            {
                // Щелчок — async void: ошибка, ушедшая отсюда, уронила бы программу.
                Serilog.Log.Error(ex, "Кнопка настройки {Extension}.{Setting}", extension.Id, setting.Key);
                await ConfirmDialog.Info($"{button.Text}: не получилось — {ex.Message}").ShowDialog(owner);
            }
            finally
            {
                run.IsEnabled = true;
            }
        };

        return run;
    }

    // ── О программе ──────────────────────────────────────────────────────────
    //
    // Дата сборки = время последней записи бинаря на диск (детерминированные сборки
    // обнуляют PE-таймстамп, поэтому берем mtime файла). Environment.ProcessPath —
    // путь к запущенному .exe; работает одинаково в обычной и single-file-сборке
    // (в отличие от Assembly.Location, который в single-file пуст и дает warning IL3000).
    // Так дата всегда отражает текущий бинарь и видна в раздаваемой версии — без ручного указания.
    /// <summary>
    /// Чем эта сборка отличается от обычной; пусто — ничем. Две особые сборки независимы:
    /// «куда программа ходит» и «кого она к себе пускает» — разные вопросы, и сочетают их по
    /// обстоятельствам.
    /// </summary>
    private static string BuildFlavour()
    {
        var parts = new List<string>(2);

        if (SmartBase.Integrations.EgressGate.ClosedLoopBuild)  parts.Add("закрытый контур");
        if (!SmartBase.Integrations.IngressGate.ListenerBuild)   parts.Add("без слушателя");

        return string.Join(", ", parts);
    }

    private static DateTime? BuildDate()
    {
        try
        {
            var path = Environment.ProcessPath;
            return string.IsNullOrEmpty(path) ? null : System.IO.File.GetLastWriteTime(path);
        }
        catch { return null; }
    }

    // Открыть папку с данными в системном файловом менеджере. UseShellExecute=true
    // делегирует системе: open (macOS), explorer (Windows), xdg-open (Linux) — надежно
    // работает с путями с пробелами/спецсимволами (Avalonia.Launcher на таких молчал).
    private void OnOpenDataFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = AppSettings.AppDataDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось открыть папку данных в проводнике: {Dir:l}", AppSettings.AppDataDir);
        }
    }

    // ── Обновления ───────────────────────────────────────────────────────────
    //
    // Этап 1–2 автообновления: путь к папке с новой версией хранится в app.db, кнопка
    // «Проверить» читает версию удаленного SmartBase.exe (FileVersionInfo, без скачивания)
    // и сравнивает с текущей. Скачивание и замену .exe выполнит отдельный SmartBase-Setup.exe
    // (следующие этапы — см. docs/guides/auto-update.md).

    private enum UpdateCheck { Available, UpToDate, Error }

    // Результат проверки: тип, сообщение и (если есть обновление) путь к файлу и его версия.
    private sealed record UpdateInfo(UpdateCheck Result, string Message,
                                     string? RemoteExe = null, Version? Remote = null);

    // Автосохранение пути при потере фокуса полем.
    private void OnUpdatePathChanged(object? sender, RoutedEventArgs e) =>
        AppSettings.SaveUpdatePath(UpdatePathBox.Text ?? "");

    // Тумблер «Автообновление»: сохраняем сразу при переключении.
    private void OnAutoUpdateToggled(object? sender, RoutedEventArgs e) =>
        AppSettings.SaveAutoUpdate(AutoUpdateToggle.IsChecked == true);

    // ── Заметки ──────────────────────────────────────────────────────────────

    // Автосохранение пути при потере фокуса полем — как у «Пути обновления».
    private void OnNotesVaultChanged(object? sender, RoutedEventArgs e) =>
        ApplyNotesVault(NotesVaultBox.Text ?? "");

    private async void OnNotesVaultBrowse(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;

        // Открываем пикер там, где стоит текущее хранилище: менять папку чаще всего нужно
        // по соседству с прежней.
        IStorageFolder? start = null;
        var current = (NotesVaultBox.Text ?? "").Trim();
        if (current.Length > 0 && Directory.Exists(current))
            start = await top.StorageProvider.TryGetFolderFromPathAsync(current);

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title                  = "Папка хранилища заметок",
            AllowMultiple          = false,
            SuggestedStartLocation = start,
        });

        var folder = folders.FirstOrDefault();
        if (folder is null) return;

        NotesVaultBox.Text = folder.Path.LocalPath;
        ApplyNotesVault(NotesVaultBox.Text);
    }

    private void OnNotesVaultClear(object? sender, RoutedEventArgs e)
    {
        NotesVaultBox.Text = "";
        ApplyNotesVault("");
    }

    private void ApplyNotesVault(string path)
    {
        var trimmed = (path ?? "").Trim();
        AppSettings.SaveNotesVault(trimmed);
        RefreshNotesVaultStatus();
        NotesVaultChanged?.Invoke(this, trimmed);
    }

    // Папка задана, но ее нет — сказать об этом здесь, а не оставлять человека гадать
    // над пустым деревом в самом разделе.
    private void RefreshNotesVaultStatus()
    {
        var path = AppSettings.LoadNotesVault();
        if (path.Length == 0)
        {
            NotesVaultStatus.IsVisible = false;
            return;
        }

        NotesVaultStatus.Text = Directory.Exists(path)
            ? "Папка найдена — раздел «Заметки» показывает ее содержимое."
            : "Папка не найдена. Проверьте путь или выберите другую.";
        NotesVaultStatus.IsVisible = true;
    }

    // Тихая проверка при запуске (тумблер «Автообновление», вызывает MainWindow.Opened):
    // путь пуст/папка недоступна/версия актуальна — никаких окон, приложение стартует как
    // обычно (статус все же пишем — виден, если открыть настройки); есть обновление — та же
    // модалка «Обновить/Отмена» и штатный поток загрузки/самозамены, что и по кнопке.
    //
    // Метод НИКОГДА не бросает: его зовут из обработчика Opened (async void), и любое
    // исключение оттуда прилетает пользователю крэш-окном вместо обычного запуска — так
    // «Cannot show window with non-visible owner» и вылезал при автозапуске в фоне.
    //
    // Проверка идет без раздела (он строится при первом показе — см. MainWindow.Sections): итог
    // запоминается и показывается, когда раздел откроют; true — нашлось обновление, и окно
    // зовет StartFoundUpdateAsync уже у построенного раздела.
    private static (string Text, bool IsError)? _startupStatus;
    private static UpdateInfo? _startupFound;

    public static async Task<bool> CheckForUpdatesOnStartupAsync()
    {
        try
        {
            var folder = AppSettings.LoadUpdatePath().Trim();
            if (folder.Length == 0) return false;

            var info = await Task.Run(() => CheckRemoteVersion(folder));
            _startupStatus = (info.Message, info.Result == UpdateCheck.Error);
            // Результат проверки — в журнал целиком, а не только сбой: «обновление лежит в папке
            // с понедельника, а у пользователя старая версия» иначе не отличить от «папка недоступна».
            switch (info.Result)
            {
                case UpdateCheck.Available:
                    Serilog.Log.Information("Автопроверка обновления: доступно {Remote:l} (у нас {Current:l}), папка {Folder:l}",
                                            info.Remote?.ToString() ?? "новая сборка", AppSettings.AppVersion, folder);
                    break;
                case UpdateCheck.UpToDate:
                    Serilog.Log.Information("Автопроверка обновления: версия {Current:l} актуальна, папка {Folder:l}",
                                            AppSettings.AppVersion, folder);
                    break;
                default:
                    Serilog.Log.Warning("Автопроверка обновления не удалась: {Message:l} (папка {Folder:l})", info.Message, folder);
                    break;
            }
            if (info.Result == UpdateCheck.Available && info.RemoteExe is not null && info.Remote is not null)
            {
                _startupFound = info;
                return true;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Автопроверка обновления при запуске не удалась");
            _startupStatus = ($"Не удалось проверить обновление: {ex.Message}", true);
        }
        return false;
    }

    /// <summary>Окно «Обновить» по найденному при запуске обновлению (см.
    /// <see cref="CheckForUpdatesOnStartupAsync"/>). Не бросает — зовется из Opened.</summary>
    public async Task StartFoundUpdateAsync()
    {
        ApplyStartupUpdateStatus();
        if (_startupFound is not { RemoteExe: { } exe, Remote: { } remote }) return;
        try { await StartUpdateAsync(exe, remote); }
        catch (Exception ex) { Serilog.Log.Error(ex, "Автообновление при запуске не удалось"); }
    }

    // Итог проверки при запуске — строкой в разделе (раздел построен позже проверки или раньше).
    private void ApplyStartupUpdateStatus()
    {
        if (_startupStatus is { } st) SetUpdateStatus(st.Text, st.IsError);
    }

    private async void OnCheckUpdateClick(object? sender, RoutedEventArgs e)
    {
        var folder = (UpdatePathBox.Text ?? "").Trim();
        AppSettings.SaveUpdatePath(folder);

        if (folder.Length == 0)
        {
            SetUpdateStatus("Укажите путь к папке с обновлением.", isError: true);
            return;
        }

        UpdateCheckBtn.IsEnabled = false;
        SetUpdateStatus("Проверка…");
        try
        {
            // Фоном, чтобы UI не подвисал на сетевом пути. Если путь недоступен —
            // вернется ошибка с красным уведомлением (как «файл недоступен» в «Управлении БД»).
            var info = await Task.Run(() => CheckRemoteVersion(folder));
            SetUpdateStatus(info.Message, isError: info.Result == UpdateCheck.Error);
            if (info.Result == UpdateCheck.Available && info.RemoteExe is not null && info.Remote is not null)
                await StartUpdateAsync(info.RemoteExe, info.Remote);
        }
        finally
        {
            UpdateCheckBtn.IsEnabled = true;
        }
    }

    // Читает версию/дату артефакта обновления в папке и сравнивает с текущей (без скачивания).
    private static UpdateInfo CheckRemoteVersion(string folder)
    {
        try
        {
            if (!System.IO.Directory.Exists(folder))
                return new(UpdateCheck.Error, "Папка обновления недоступна — проверьте путь и подключение к сети.");

            // Источник обновления — SmartBase-Setup (один артефакт на сервере работает и как
            // установщик, и как обновление); если его нет — берем обычный SmartBase. На macOS
            // артефакт — это .app-бандл (папка), а сравниваемый по дате файл — внутренний бинарь.
            if (!ResolveRemote(folder, out var remoteArtifact, out var remoteTimeFile, out var missing))
                return new(UpdateCheck.Error, missing);

            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            var cur     = Normalize(current);   // Major.Minor.Build — как версия в «О программе»

            // Версию читаем из удаленного файла (на Windows — из PE-ресурса .exe). Если версия
            // прочиталась и отличается — решаем по ней. На Linux/macOS у нативного бинаря
            // версия из файла недоступна → ниже падаем на сравнение по дате изменения.
            if (Version.TryParse(FileVersionInfo.GetVersionInfo(remoteTimeFile).FileVersion, out var remoteFull))
            {
                var remote = Normalize(remoteFull);
                if (remote > cur)
                    return new(UpdateCheck.Available, $"Доступна версия {remote} (у вас {cur}).", remoteArtifact, remote);
                if (remote < cur)
                    return new(UpdateCheck.UpToDate, $"У вас актуальная версия ({cur}).");
                // равны → сравниваем дату изменения файла ниже
            }

            // Версия недоступна или равна → ловим измененный файл по СОДЕРЖИМОМУ, чтобы можно
            // было раздавать сборки без смены версии:
            //   • другой размер = точно другая сборка (дешево, только метаданные, надежнее даты —
            //     не зависит от часов машин и от копирования с сохранением атрибутов);
            //   • иначе — дата изменения новее на сервере (допуск 2 с против округления меток).
            // После штатного обновления размер установленного == серверному, а дата установленного
            // (время загрузки) уже позже сборки — ложных «есть обновление» не будет.
            var localExe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(localExe) && System.IO.File.Exists(localExe))
            {
                var local  = new System.IO.FileInfo(localExe);
                var remote = new System.IO.FileInfo(remoteTimeFile);
                if (remote.Length != local.Length)
                    return new(UpdateCheck.Available, $"Доступна новая сборка (файл изменен, {cur}).", remoteArtifact, cur);
                if (remote.LastWriteTimeUtc > local.LastWriteTimeUtc.AddSeconds(2))
                    return new(UpdateCheck.Available, $"Доступна новая сборка (файл новее, {cur}).", remoteArtifact, cur);
            }
            return new(UpdateCheck.UpToDate, $"У вас актуальная версия ({cur}).");
        }
        catch (Exception ex)
        {
            return new(UpdateCheck.Error, $"Не удалось проверить обновление: {ex.Message}");
        }

        static Version Normalize(Version v) => new(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);
    }

    // Находит артефакт обновления в папке: на Windows/Linux — один файл (он же сравнивается по
    // версии/дате), на macOS — .app-бандл (папка) + внутренний бинарь для сравнения по дате.
    private static bool ResolveRemote(string folder, out string artifact, out string timeFile, out string error)
    {
        artifact = timeFile = error = "";
        if (Setup.InstallService.IsMac)
        {
            var setupB = System.IO.Path.Combine(folder, Setup.InstallService.SetupBundleName);
            var plainB = System.IO.Path.Combine(folder, Setup.InstallService.AppBundleName);
            var bundle = System.IO.Directory.Exists(setupB) ? setupB
                       : System.IO.Directory.Exists(plainB) ? plainB : null;
            if (bundle is null)
            {
                error = $"В папке нет {Setup.InstallService.SetupBundleName} (или {Setup.InstallService.AppBundleName}).";
                return false;
            }
            artifact = bundle;
            timeFile = Setup.InstallService.MacInnerBinary(bundle);
            return true;
        }

        var setup = System.IO.Path.Combine(folder, Setup.InstallService.SetupExeName);
        var plain = System.IO.Path.Combine(folder, Setup.InstallService.AppExeName);
        var file  = System.IO.File.Exists(setup) ? setup
                  : System.IO.File.Exists(plain) ? plain : null;
        if (file is null)
        {
            error = $"В папке нет {Setup.InstallService.SetupExeName} (или {Setup.InstallService.AppExeName}).";
            return false;
        }
        artifact = timeFile = file;
        return true;
    }

    // Подтверждение → загрузка нового артефакта во временную папку с прогрессом → запуск
    // самозамены (новый бинарь в режиме --apply-update) → закрытие приложения.
    private async Task StartUpdateAsync(string remoteExe, Version remote)
    {
        if (VisualRoot is not Window win) return;

        // Модалке нужен ВИДИМЫЙ владелец, иначе Avalonia бросает «Cannot show window with
        // non-visible owner». Окно могло уехать в трей (фоновый режим), пока шла проверка
        // сетевой папки, — возвращаем его: пользователю все равно подтверждать обновление,
        // после которого приложение закроется и откроется заново.
        if (!win.IsVisible)
        {
            win.Show();
            if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
            win.Activate();
        }

        // Текущая версия (Major.Minor.Build — как в «О программе»). При обновлении «по дате»
        // remote == текущей: не пишем «Доступна версия {та же}», а честно — «новая сборка».
        var current  = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        var currentN = new Version(current.Major, current.Minor, Math.Max(current.Build, 0));

        var msg = remote > currentN
            ? $"Доступна версия {remote}. Приложение закроется, обновится и снова откроется."
            : "Доступна новая сборка приложения. Приложение закроется, обновится и снова откроется.";
        if (await new ConfirmDialog(msg, "Обновить").ShowDialog<bool>(win) != true) return;

        // Цель замены: на macOS — наш .app-бандл целиком, иначе — сам файл бинаря.
        var processPath = Environment.ProcessPath;
        var target = Setup.InstallService.IsMac
            ? Setup.InstallService.BundleRoot(processPath)
            : processPath;
        if (string.IsNullOrEmpty(target))
        {
            SetUpdateStatus("Не удалось определить путь к программе для обновления.", isError: true);
            return;
        }

        // Обновление перезапишет артефакт в папке установки — без прав на запись (напр. Program
        // Files без администратора) самозамена молча не сработает. Проверяем заранее: не качаем и
        // НЕ закрываем приложение, показываем понятную ошибку.
        var installDir = System.IO.Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(installDir) || !Setup.InstallService.CanCreateOrWrite(installDir))
        {
            SetUpdateStatus("Обновление невозможно: нет прав на запись в папку установки. Переустановите приложение в доступную папку или запустите от имени администратора.", isError: true);
            return;
        }

        // Temp-артефакт: .app-бандл (папка) на macOS, иначе файл.
        var tempName = OperatingSystem.IsWindows() ? $"{AppSettings.AppName}.update.exe"
                     : Setup.InstallService.IsMac   ? $"{AppSettings.AppName}.update.app"
                     : $"{AppSettings.AppName}.update";
        var tempExe  = System.IO.Path.Combine(System.IO.Path.GetTempPath(), tempName);

        UpdateCheckBtn.IsEnabled = false;

        // Загрузка идет в отдельном ОКНЕ с процентами (единый ProgressDialog, как импорт),
        // а не инлайн-полосой в настройках. Окно нельзя закрыть, пока копирование не кончится.
        var progressDlg = new ProgressDialog { Title = "Обновление" };
        progressDlg.SetProgress(0, "Загрузка обновления…");
        var dlgClosed = progressDlg.ShowDialog<object?>(win);
        try
        {
            var progress = new Progress<double>(p => progressDlg.SetProgress(p, "Загрузка обновления…"));
            if (!await DownloadAsync(remoteExe, tempExe, progress))
            {
                SetUpdateStatus("Загрузка прервалась — артефакт скопирован не полностью. Попробуйте еще раз.", isError: true);
                return;
            }
            // На Unix temp-копию нужно сделать запускаемой (на macOS — внутренний бинарь бандла).
            Setup.InstallService.MakeExecutable(
                Setup.InstallService.IsMac ? Setup.InstallService.MacInnerBinary(tempExe) : tempExe);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Не удалось загрузить обновление из {Source:l}", remoteExe);
            SetUpdateStatus($"Не удалось загрузить обновление: {ex.Message}", isError: true);
            return;
        }
        finally
        {
            progressDlg.Done();
            await dlgClosed;
            UpdateCheckBtn.IsEnabled = true;
        }

        // Сверяем скачанный артефакт перед заменой (размер уже проверен в DownloadAsync). На
        // Windows — версия из PE: не даем «обновиться» на файл СТАРЕЕ текущего (файл в папке мог
        // смениться между проверкой и загрузкой). У нативных бинарей Linux/macOS версия из файла
        // недоступна — там полагаемся на размер/дату, как в CheckRemoteVersion.
        if (OperatingSystem.IsWindows()
            && Version.TryParse(FileVersionInfo.GetVersionInfo(tempExe).FileVersion, out var downloaded)
            && new Version(downloaded.Major, downloaded.Minor, Math.Max(downloaded.Build, 0)) < currentN)
        {
            SetUpdateStatus("Скачанный файл оказался старее текущей версии — обновление отменено.", isError: true);
            return;
        }

        try
        {
            // Запускаем апдейтер: на macOS это внутренний бинарь temp-бандла, иначе сам temp-файл.
            var helper = Setup.InstallService.IsMac ? Setup.InstallService.MacInnerBinary(tempExe) : tempExe;
            Process.Start(new ProcessStartInfo
            {
                FileName        = helper,
                UseShellExecute = false,
                Arguments       = $"--apply-update --target \"{target}\" --pid {Environment.ProcessId}",
            });
            Serilog.Log.Information("Обновление начато: {Source:l} → {Target:l}, приложение закрывается для самозамены (версия {Remote:l})",
                                    remoteExe, target, remote);
            Program.ExitReason = "перезапуск для обновления";
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Не удалось запустить апдейтер из {Temp:l}", tempExe);
            SetUpdateStatus($"Не удалось запустить обновление: {ex.Message}", isError: true);
            return;
        }

        // Закрываем приложение: апдейтер дождется выхода, заменит артефакт и перезапустит.
        (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    // Копирование с прогрессом (сеть → temp). true, если скопировано целиком.
    private static async Task<bool> DownloadAsync(string sourceExe, string tempExe, IProgress<double> progress)
    {
        // macOS: источник — .app-бандл (папка) → копируем дерево целиком (publish не single-file).
        if (System.IO.Directory.Exists(sourceExe))
        {
            if (System.IO.Directory.Exists(tempExe)) Setup.InstallService.DeleteRecursive(tempExe);
            await Setup.InstallService.CopyTreeAsync(sourceExe, tempExe, progress.Report);
            return System.IO.File.Exists(Setup.InstallService.MacInnerBinary(tempExe));
        }

        const int bufferSize = 1 << 20; // 1 МБ
        await using var src = new System.IO.FileStream(sourceExe, System.IO.FileMode.Open,
            System.IO.FileAccess.Read, System.IO.FileShare.Read, bufferSize, useAsync: true);
        await using var dst = new System.IO.FileStream(tempExe, System.IO.FileMode.Create,
            System.IO.FileAccess.Write, System.IO.FileShare.None, bufferSize, useAsync: true);

        var total  = src.Length;
        var buffer = new byte[bufferSize];
        long copied = 0;
        int read;
        while ((read = await src.ReadAsync(buffer)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read));
            copied += read;
            if (total > 0) progress.Report((double)copied / total);
        }
        await dst.FlushAsync();
        return total == 0 || copied == total;
    }

    private void SetUpdateStatus(string text, bool isError = false)
    {
        UpdateStatusText.Text      = text;
        UpdateStatusText.IsVisible = !string.IsNullOrEmpty(text);
        var key = isError ? "BrushRed" : "BrushSecondaryText";
        if (this.TryFindResource(key, out var res) && res is Avalonia.Media.IBrush brush)
            UpdateStatusText.Foreground = brush;
    }

    // ── Уровень масштабирования ──────────────────────────────────────────────
    //
    // Масштаб применяется ко всему окну: MainWindow слушает Scaling.ScaleChanged
    // и ставит LayoutTransform на корень. Слайдер — в процентах, Scaling хранит множитель.
    // Во время перетаскивания НЕ применяем (окно перекладывается и ползунок «уезжает»
    // из-под курсора) — только двигаем пузырек; применение — в EndScaleDrag.
    private void OnScaleChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_scaleDragging)
        {
            UpdateScaleBadge();
            return;
        }
        Scaling.SetScale(Math.Round(e.NewValue) / 100.0);
        UpdateScaleBadge();
    }

    // Отпускание ползунка (или клика по треку): применяем итоговое значение.
    private void EndScaleDrag()
    {
        if (!_scaleDragging) return;
        _scaleDragging = false;
        Scaling.SetScale(Math.Round(ScaleSlider.Value) / 100.0);
        UpdateScaleBadge();
    }

    private void OnScaleSliderEntered(object? sender, PointerEventArgs e)
    {
        UpdateScaleBadge();
        ScaleValuePopup.IsOpen = true;
    }

    private void OnScaleSliderExited(object? sender, PointerEventArgs e) => ScaleValuePopup.IsOpen = false;

    // Движение по слайдеру: при перетаскивании пузырек следует за ползунком, при
    // простом наведении — показывает масштаб, который БЫЛ БЫ в точке под курсором.
    private void OnScaleSliderMoved(object? sender, PointerEventArgs e)
    {
        if (_scaleDragging) UpdateScaleBadge();
        else                ShowScaleBadge(ValueAtPointer(ScaleSlider, e, step: 10));
    }

    private void UpdateScaleBadge() => ShowScaleBadge(ScaleSlider.Value);

    // Текст = масштаб в процентах; смещение пузырька — под точку этого значения на треке.
    private void ShowScaleBadge(double value)
    {
        ScaleValueText.Text = $"{(int)Math.Round(value)} %";

        var range = ScaleSlider.Maximum - ScaleSlider.Minimum;
        var w     = ScaleSlider.Bounds.Width;
        if (range <= 0 || w <= 0) return;

        var frac = (value - ScaleSlider.Minimum) / range;
        const double thumbW = 26;
        // Ползунок масштабируется LayoutTransform'ом окна, а попап живет в немасштабированном
        // оверлее → смещение домножаем на текущий масштаб, иначе плашка «уезжает» от ползунка.
        ScaleValuePopup.HorizontalOffset = (frac * (w - thumbW) + thumbW / 2 - w / 2) * Scaling.Current;
    }

    // Значение слайдера в точке под курсором: обратная формула к смещению пузырька
    // (трек = ширина минус ползунок), с защелкиванием на шаг слайдера.
    private static double ValueAtPointer(Slider s, PointerEventArgs e, double step)
    {
        var w = s.Bounds.Width;
        const double thumbW = 26;
        if (w <= thumbW) return s.Value;

        var x    = e.GetPosition(s).X;
        var frac = Math.Clamp((x - thumbW / 2) / (w - thumbW), 0, 1);
        var raw  = s.Minimum + frac * (s.Maximum - s.Minimum);
        return Math.Clamp(Math.Round(raw / step) * step, s.Minimum, s.Maximum);
    }

    // Кнопка-стрелка: подтверждение в стандартном диалоге → возврат к 100 %.
    private async void OnScaleReset(object? sender, RoutedEventArgs e)
    {
        if (VisualRoot is not Window win) return;
        var dlg = new ConfirmDialog("Вернуть уровень масштабирования по умолчанию?", "Сбросить");
        if (await dlg.ShowDialog<bool>(win) != true) return;

        ScaleSlider.Value = AppSettings.ScaleDefault * 100;   // → OnScaleChanged → SetScale(1.0)
    }

    // Размер шрифта: дельта пишется в Application.Resources (DynamicResource),
    // C#-виды пересобираются по событию FontScale.Changed. Во время перетаскивания
    // НЕ применяем (перекладка интерфейса дергает ползунок) — только пузырек;
    // применение — в EndFontDrag.
    private void OnFontSizeChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_fontDragging)
        {
            UpdateFontBadge();
            return;
        }
        FontScale.SetDelta((int)Math.Round(e.NewValue));
        UpdateFontBadge();
    }

    // Отпускание ползунка (или клика по треку): применяем итоговую дельту.
    private void EndFontDrag()
    {
        if (!_fontDragging) return;
        _fontDragging = false;
        FontScale.SetDelta((int)Math.Round(FontSizeSlider.Value));
        UpdateFontBadge();
    }

    // Пузырек со значением показываем при наведении/перетаскивании ползунка.
    private void OnFontSliderEntered(object? sender, PointerEventArgs e)
    {
        UpdateFontBadge();
        FontValuePopup.IsOpen = true;
    }

    private void OnFontSliderExited(object? sender, PointerEventArgs e) => FontValuePopup.IsOpen = false;

    // Движение по слайдеру: при перетаскивании пузырек следует за ползунком, при
    // наведении — показывает размер, который БЫЛ БЫ в точке под курсором.
    private void OnFontSliderMoved(object? sender, PointerEventArgs e)
    {
        if (_fontDragging) UpdateFontBadge();
        else               ShowFontBadge(ValueAtPointer(FontSizeSlider, e, step: 1));
    }

    private void UpdateFontBadge() => ShowFontBadge(FontSizeSlider.Value);

    // Текст = итоговый базовый размер (стандарт ± дельта); смещение пузырька — под
    // точку этого значения на треке. Плашка постоянного размера (как у масштаба) —
    // меняется только число.
    private void ShowFontBadge(double deltaValue)
    {
        var delta     = (int)Math.Round(deltaValue);
        var effective = FontScale.BaseFontSize + delta;            // итоговый размер шрифта
        FontValueText.Text = ((int)Math.Round(effective)).ToString();

        var range = FontSizeSlider.Maximum - FontSizeSlider.Minimum;
        var w     = FontSizeSlider.Bounds.Width;
        if (range <= 0 || w <= 0) return;

        var frac = (deltaValue - FontSizeSlider.Minimum) / range;
        const double thumbW = 26;
        // Тот же учет масштаба окна, что и у плашки масштабирования (попап — в оверлее).
        FontValuePopup.HorizontalOffset = (frac * (w - thumbW) + thumbW / 2 - w / 2) * Scaling.Current;
    }

    // Кнопка-стрелка: подтверждение в стандартном диалоге → возврат к стандарту (дельта 0).
    private async void OnFontSizeReset(object? sender, RoutedEventArgs e)
    {
        if (VisualRoot is not Window win) return;
        var dlg = new ConfirmDialog("Вернуть размер текста по умолчанию?", "Сбросить");
        if (await dlg.ShowDialog<bool>(win) != true) return;

        FontSizeSlider.Value = AppSettings.FontDeltaDefault;   // → OnFontSizeChanged → SetDelta(0)
    }

    // Вызывается из MainWindow: реестр + способ узнать, смонтирована ли БД сейчас,
    // + статус фоновой очереди монтирования для еще не подключенных, + сведения о файле
    // смонтированной SQLite-базы (снятые при монтировании, без обращения к файлу отсюда)
    // и признак «текущий пользователь — администратор этой базы» (он обслуживает файл и
    // правит в окне настроек название с паролем подключения).
    /// <param name="taskStages">Стадии задач всех допущенных баз — для списка «Стадия новой
    /// задачи» в разделе «Задачи»; null — только три стандартные.</param>
    /// <param name="databases">Открытые базы, к которым допущен текущий пользователь. Зовется
    /// каждый раз заново, а не берется списком: базы монтируются и отцепляются на ходу, а
    /// «Интеграции» укладывают ответ в коллекцию выбранной базы и обязаны видеть их состав
    /// таким, каков он сейчас.</param>
    /// <param name="ingress">Служба приема по сети — открывает и закрывает порт. Передается
    /// самой службой, а не парой обратных вызовов: экрану нужны и состояние, и действие, и
    /// расщеплять их значило бы завести два мнения о том, слушаем ли мы сейчас.</param>
    /// <param name="inboxSweep">Разобрать папку обмена немедленно. Ведет это служба приема
    /// (<c>InboxService</c>) — та же, что ходит по расписанию: две реализации одного обхода
    /// разошлись бы на первой же правке.</param>
    public void Initialize(DatabaseRegistry registry, Func<int, bool> isMounted,
                           Func<int, (string Text, bool Failed)?>? queueStatus = null,
                           Func<int, DatabaseManager.StorageStats?>? storage = null,
                           Func<int, bool>? isDbAdmin = null,
                           Func<IReadOnlyList<string>>? taskStages = null,
                           Func<IReadOnlyList<DatabaseManager>>? databases = null,
                           Func<CancellationToken, Task<InboxRun>>? inboxSweep = null,
                           Func<Scenario, CancellationToken, Task<ScenarioOutcome>>? scenarioRun = null,
                           IngressService? ingress = null,
                           StreamService? streams = null)
    {
        _registry    = registry;
        _isMounted   = isMounted;
        _queueStatus = queueStatus;
        _storage     = storage;
        _taskStages  = taskStages;
        _isDbAdmin   = isDbAdmin;
        _databases   = databases;
        _inboxSweep  = inboxSweep;
        _scenarioRun = scenarioRun;
        _ingress     = ingress;
        _streams     = streams;
        RefreshDatabaseList();
    }

    private Func<int, DatabaseManager.StorageStats?>? _storage;
    private Func<int, bool>?                          _isDbAdmin;
    private Func<IReadOnlyList<DatabaseManager>>?     _databases;
    private Func<CancellationToken, Task<InboxRun>>? _inboxSweep;
    private Func<Scenario, CancellationToken, Task<ScenarioOutcome>>? _scenarioRun;
    // Служба приема по сети: экран ее включает, а она отвечает, слушает или нет и почему.
    private IngressService? _ingress;
    // Служба потоков событий: экран спрашивает у нее, слушается ли поток, и зовет сверку после
    // правки карточек — чтобы слушание перезапустилось сразу, а не через полминуты.
    private StreamService? _streams;

    // Перестроить список (статус монтирования мог измениться). Вызывается из MainWindow.
    public void RefreshDatabases() => RefreshDatabaseList();

    // Живое обновление статуса строки БД из очереди монтирования: проценты и время
    // («Обновление · 45% · ≈ 2 мин») меняются каждые полсекунды НА МЕСТЕ, без пересборки
    // списка — как у заглушек сайдбара. Вызывается из MainWindow на каждый тик очереди.
    public void UpdateQueueStatus(int dbId, string text, bool failed)
    {
        if (DatabaseList.ItemsSource is not IEnumerable<DatabaseRow> rows) return;
        foreach (var r in rows)
            if (r.Id == dbId && !r.IsMounted)
            {
                r.SetStatus(text, failed);
                break;
            }
    }

    // Временная надпись у СМОНТИРОВАННОЙ базы («снова доступна» после возврата — см.
    // MainWindow.ShowReturnedDbStatus). Список пересобирается часто (каждое монтирование),
    // поэтому текст хранится и применяется при каждой пересборке, пока его не снимут (null).
    private readonly Dictionary<int, string> _mountedStatus = [];

    public void SetDatabaseStatus(int dbId, string? text)
    {
        if (text is null) _mountedStatus.Remove(dbId);
        else              _mountedStatus[dbId] = text;
        if (DatabaseList.ItemsSource is not IEnumerable<DatabaseRow> rows) return;
        foreach (var r in rows)
            if (r.Id == dbId && r.IsMounted)
            {
                r.SetStatus(text, false);
                break;
            }
    }

    // Переключение раздела: заголовок + видимость соответствующей панели.
    public void ShowCategory(string key)
    {
        // Порог медленности: раздел настроек (списки, панели, инструкция) дольше Diag.UiOpen — в журнал.
        using var slow = Logging.Diag.Measure($"открытие настроек «{(SettingsRegistry.FindByKey(key) ?? SettingsRegistry.All[0]).Label}»",
                                              Logging.Diag.UiOpen);
        GeneralPanel.IsVisible    = key == "general";
        AppearancePanel.IsVisible = key == "appearance";
        DatabasePanel.IsVisible   = key == "database";
        BackupPanel.IsVisible     = key == "backup";
        CalendarPanel.IsVisible   = key == "calendar";
        TasksPanel.IsVisible      = key == "tasks";
        LibraryPanel.IsVisible    = key == "library";
        ConstructorPanel.IsVisible = key == "constructor";
        ExtensionsPanel.IsVisible = key == "extensions";
        IntegrationsPanel.IsVisible = key == "integrations";
        GuidePanel.IsVisible      = key == "guide";
        NotesPanel.IsVisible      = key == "notes";
        ShortcutsPanel.IsVisible  = key == "shortcuts";
        LogsPanel.IsVisible       = key == "logs";
        AboutPanel.IsVisible      = key == "about";
        // Панель деталей журнала живет в корне (во всю высоту) — при уходе из «Диагностики»
        // ее нужно закрыть явно (она не вложена в LogsPanel).
        if (key != "logs") CloseLogsDetail();
        // То же и у «Содержания» инструкции: панель, разделитель и ручка лежат в корне.
        ShowGuideToc(key == "guide");

        // Заголовок берем из реестра по ключу — единственный источник подписей разделов.
        HeaderBar.Title = (SettingsRegistry.FindByKey(key) ?? SettingsRegistry.All[0]).Label;
        // Кнопка «Добавить базу данных» в навбаре осмысленна только в разделе БД.
        HeaderBar.ShowDatabaseActions = key == "database";
        HeaderBar.ShowLogsActions     = key == "logs";
        HeaderBar.ShowIntegrationTabs = key == "integrations";
        // Поиск по действиям нужен только своему разделу; уход очищает строку отбора.
        HeaderBar.ShowShortcutSearch  = key == "shortcuts";

        if (key == "logs")       RebuildLogsPanel();
        if (key == "guide")      BuildGuide();
        if (key == "extensions") BuildExtensionsPanel();
        if (key == "integrations") BuildIntegrationsPanel();
        if (key == "backup")     RebuildBackupPanel();
        if (key == "calendar")   RebuildCalendarPanel();
        if (key == "tasks")      RebuildTasksPanel();
        if (key == "library")    RebuildLibraryPanel();
        if (key == "constructor") RebuildConstructorPanel();
        if (key == "shortcuts")  RebuildShortcutsPanel();
    }

    // ── Управление базой данных ──────────────────────────────────────────────

    private void RefreshDatabaseList()
    {
        if (_registry is null) { DatabaseList.ItemsSource = null; return; }

        DatabaseList.ItemsSource = _registry.GetAll().Select(d =>
        {
            var mounted = _isMounted?.Invoke(d.Id) ?? true;
            // Не смонтирована: либо ждет/монтируется в фоновой очереди (живой статус),
            // либо недоступна (нет в очереди или очередь пометила ошибкой).
            var queue = mounted ? null : _queueStatus?.Invoke(d.Id);
            return new DatabaseRow
            {
                Id            = d.Id,
                Name          = d.Name,
                Path          = d.Path,   // у PG-записей — «postgres://host:port/db», без пароля
                IsDefault     = d.IsDefault,
                IsMounted     = mounted,
                IsPostgres    = d.IsPostgres,
                // Сведения о файле и право на «Сжать файл» — только у смонтированной
                // SQLite-базы (PG-файла нет; отцепленную не трогаем).
                Storage       = mounted && !d.IsPostgres ? _storage?.Invoke(d.Id) : null,
                CanMaintain   = mounted && !d.IsPostgres && (_isDbAdmin?.Invoke(d.Id) ?? false),
                StatusText    = mounted ? _mountedStatus.GetValueOrDefault(d.Id) : queue?.Text
                    ?? (d.IsPostgres ? "Не подключена (сервер недоступен)"
                                     : "Не подключена (файл недоступен)"),
                StatusIsError = !mounted && (queue is null || queue.Value.Failed),
            };
        }).ToList();
    }

    // Создать новую БД: собственное русское окно (имя + папка) вместо системного
    // save-диалога — тот говорит на языке ОС и предлагает ПЕРЕЗАПИСАТЬ существующий
    // файл; здесь занятое имя дает ошибку «уже существует», перезаписи нет в принципе.
    // Схема создается при монтировании.
    private async void OnCreateDatabaseClick(object? sender, RoutedEventArgs e)
    {
        if (_registry is null || VisualRoot is not Window win) return;

        // Стартовая папка — «Документы» (или домашняя, если «Документов» нет).
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(docs) || !Directory.Exists(docs))
            docs = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var path = await new CreateDatabaseDialog(docs).ShowDialog<string?>(win);
        if (path is null) return;

        RegisterDatabase(path);
    }

    // Подключить существующий .db-файл (например, общий в сетевой папке).
    private async void OnConnectDatabaseClick(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (_registry is null || top is null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Подключить базу данных",
            AllowMultiple  = false,
            FileTypeFilter = [new FilePickerFileType("База данных SQLite") { Patterns = ["*.db"] }],
        });
        var file = files.FirstOrDefault();
        if (file is null) return;

        // Уже в реестре — не создаем дубль и честно говорим об этом (раньше клик
        // просто «ничего не делал»).
        var full = System.IO.Path.GetFullPath(file.Path.LocalPath);
        if (_registry.GetAll().Any(d => string.Equals(d.Path, full, StringComparison.OrdinalIgnoreCase)))
        {
            if (VisualRoot is Window win)
                await ConfirmDialog.Info(
                    $"База данных «{System.IO.Path.GetFileNameWithoutExtension(full)}» уже подключена.")
                    .ShowDialog(win);
            return;
        }

        RegisterDatabase(full);
    }

    private void RegisterDatabase(string path)
    {
        if (_registry is null) return;
        var name  = System.IO.Path.GetFileNameWithoutExtension(path);
        var entry = _registry.Add(name, path);
        // Монтирование — в фоне (MainWindow, с окном «Подключение…» при >400 мс); список
        // строим сразу со статусом «не подключена», по завершении его обновит RefreshDatabases.
        DatabaseAdded?.Invoke(this, entry);
        RefreshDatabaseList();
    }

    // Подключить серверную базу PostgreSQL: русский диалог с проверкой соединения
    // (см. PgConnectDialog). Пара сервер+порт+база уже в реестре — честное «уже подключена».
    private async void OnConnectPostgresClick(object? sender, RoutedEventArgs e)
    {
        if (_registry is null || VisualRoot is not Window win) return;

        var result = await new PgConnectDialog().ShowDialog<PgConnectResult?>(win);
        if (result is null) return;

        var info = result.Info;
        if (_registry.GetAll().Any(d => d.IsPostgres
                && string.Equals(d.PgHost, info.Host, StringComparison.OrdinalIgnoreCase)
                && (d.PgPort ?? PgConnectionInfo.DefaultPort) == info.Port
                && d.PgDatabase == info.Database))
        {
            await ConfirmDialog.Info(
                $"База данных «{info.Database}» на сервере {info.Host}:{info.Port} уже подключена.")
                .ShowDialog(win);
            return;
        }

        var entry = _registry.AddPostgres(result.Name, info);
        DatabaseAdded?.Invoke(this, entry);
        RefreshDatabaseList();
    }

    // Создать базу на сервере PostgreSQL из приложения: окно создает роль-владельца и базу
    // (PgCreateDatabaseDialog + PgDatabaseCreator) и возвращает уже ПРОВЕРЕННУЮ связку
    // база+роль+пароль. Дальше — как у обычного подключения; в конце показываем «Данные
    // подключения» — сводку для передачи коллегам (без пароля).
    private async void OnCreatePostgresClick(object? sender, RoutedEventArgs e)
    {
        if (_registry is null || VisualRoot is not Window win) return;

        var result = await new PgCreateDatabaseDialog().ShowDialog<PgCreateDatabaseResult?>(win);
        if (result is null) return;

        var info = result.Info;
        // Дубль возможен, если запись о базе с этим именем осталась от прежнего сервера.
        if (_registry.GetAll().Any(d => d.IsPostgres
                && string.Equals(d.PgHost, info.Host, StringComparison.OrdinalIgnoreCase)
                && (d.PgPort ?? PgConnectionInfo.DefaultPort) == info.Port
                && d.PgDatabase == info.Database))
        {
            await ConfirmDialog.Info(
                $"База данных «{info.Database}» на сервере {info.Host}:{info.Port} уже подключена.")
                .ShowDialog(win);
            return;
        }

        var entry = _registry.AddPostgres(result.Name, info);
        DatabaseAdded?.Invoke(this, entry);
        RefreshDatabaseList();

        await new PgDetailsDialog(info.Host, info.Port, info.Database, info.User, info.SslMode)
            .ShowDialog(win);
    }

    // Окно «Настройки базы данных» (шестеренка в строке): общее — переименование; у
    // SQLite — файл/сжатие, у PG — статус сервера, данные подключения, смена пароля.
    // Сжатие окно запускает колбэком через то же событие MainWindow, которым раньше
    // пользовалась кнопка строки. Резервная копия живет в разделе «Резервное копирование».
    private async void OnDbSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (_registry is null || VisualRoot is not Window win) return;
        if (sender is not Button { Tag: int id }) return;

        var entry = _registry.GetAll().FirstOrDefault(d => d.Id == id);
        if (entry is null) return;

        var mounted     = _isMounted?.Invoke(id) ?? false;
        var storage     = mounted && !entry.IsPostgres ? _storage?.Invoke(id) : null;
        var isAdmin     = _isDbAdmin?.Invoke(id) ?? false;
        var canMaintain = mounted && !entry.IsPostgres && isAdmin;
        // Название и пароль подключения правит администратор базы; у неподключенной базы
        // прав не видно, и поля открыты — иначе устаревший пароль вписать будет некому.
        var canManage   = DatabaseRow.CanManage(mounted, isAdmin);

        // Метка конфиденциальности лежит ВНУТРИ файла базы, поэтому окну нужен репозиторий
        // подключенной базы. Не подключена — строки метки в окне просто не будет: читать и
        // писать нечего, а показать поле и отказать при нажатии значило бы объяснять человеку
        // устройство хранения.
        var repo = (_databases?.Invoke() ?? []).FirstOrDefault(d => d.DbId == id)?.Constructor;

        var result = await new DbSettingsDialog(entry, storage, canMaintain, canManage,
                onCompact: () => DatabaseCompactRequested?.Invoke(this, id),
                repo: repo)
            .ShowDialog<DbSettingsResult?>(win);
        if (result is null) return;

        if (result.NewName is { } newName)
        {
            _registry.Rename(id, newName);
            DatabaseRenamed?.Invoke(this, (id, newName));
        }
        if (result.NewPgPassword is { } pwd)
        {
            _registry.UpdatePgPassword(id, pwd);
            // Недоступной базе новый пароль нужен прямо сейчас — пробуем переподключиться.
            if (!mounted) DatabaseReconnectRequested?.Invoke(this, id);
        }
        RefreshDatabaseList();
    }

    private async void OnRemoveDatabaseClick(object? sender, RoutedEventArgs e)
    {
        if (_registry is null) return;
        if (sender is not Button { Tag: int id }) return;

        var entry = _registry.GetAll().FirstOrDefault(d => d.Id == id);
        if (entry is null || entry.IsDefault) return;   // дефолт не отключаем

        if (VisualRoot is not Window win) return;
        var dlg = new ConfirmDialog(entry.IsPostgres
            ? $"Отключить базу данных «{entry.Name}»? Данные на сервере не удаляются — ее можно подключить снова."
            : $"Отключить базу данных «{entry.Name}»? Файл на диске не удаляется — ее можно подключить снова.");
        if (await dlg.ShowDialog<bool>(win) != true) return;

        _registry.Remove(id);
        DatabaseRemoved?.Invoke(this, id);
        RefreshDatabaseList();
    }

    private void OnReconnectDatabaseClick(object? sender, RoutedEventArgs e)
    {
        // MainWindow попробует смонтировать БД заново и обновит список (статус).
        if (sender is Button { Tag: int id })
            DatabaseReconnectRequested?.Invoke(this, id);
    }
}
