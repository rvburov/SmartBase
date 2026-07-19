using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SmartBase.Controls.Dialogs;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Settings;
using SmartBase.Utils;

namespace SmartBase.Views.Settings;

public partial class SettingsView : UserControl
{
    private DatabaseRegistry? _registry;
    // Статус БД в фоновой очереди монтирования (текст + признак ошибки) или null,
    // если базы в очереди нет; поставляет MainWindow из MountQueue.
    private Func<int, (string Text, bool Failed)?>? _queueStatus;
    // Признак «БД сейчас смонтирована» (даёт MainWindow по рабочему пространству).
    private Func<int, bool>?  _isMounted;

    // Ползунок удерживается мышью: применение масштаба/шрифта отложено до отпускания
    // (живое применение перекладывает окно, и ползунок «уезжает» из-под курсора).
    private bool _scaleDragging;
    private bool _fontDragging;

    // Реестр изменён — MainWindow монтирует/размонтирует БД и обновляет сайдбар.
    public event EventHandler<DatabaseRefEntity>? DatabaseAdded;
    public event EventHandler<int>?               DatabaseRemoved;
    // Попытка переподключить недоступную БД (по DbId).
    public event EventHandler<int>?               DatabaseReconnectRequested;

    public SettingsView()
    {
        InitializeComponent();

        // ── О программе ──────────────────────────────────────────────────────
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version is not null)
        {
            var text = $"Версия {version.Major}.{version.Minor}.{version.Build}";
            if (BuildDate() is { } built) text += $" · сборка {built:dd.MM.yyyy}";
            VersionText.Text = text;
        }
        DataPathText.Text   = AppSettings.AppDataDir;
        SystemInfoText.Text = $"{RuntimeInformation.OSDescription.Trim()} · {RuntimeInformation.FrameworkDescription}";
        UpdatePathBox.Text  = AppSettings.LoadUpdatePath();
        AutoUpdateToggle.IsChecked = AppSettings.LoadAutoUpdate();

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

        // Стартовый раздел: выделить сохранённый пункт (по Tag).
        SelectStartupModule(AppSettings.LoadStartupModule());

        // Стартовый вид календаря: выделить по индексу (Tag = "0".."5").
        CalendarViewCombo.SelectedIndex = AppSettings.LoadCalendarView();

        // Стартовый вид конструктора: Таблица (0) или Список (1).
        ConstructorViewCombo.SelectedIndex = AppSettings.LoadConstructorView() == "list" ? 1 : 0;

        // Меню «Добавить базу данных» живёт в навбаре (видно только в разделе БД,
        // см. ShowCategory) — пункты ведут на те же обработчики, что прежние кнопки.
        HeaderBar.CreateSqliteRequested    += (_, _) => OnCreateDatabaseClick(this, new RoutedEventArgs());
        HeaderBar.ConnectSqliteRequested   += (_, _) => OnConnectDatabaseClick(this, new RoutedEventArgs());
        HeaderBar.ConnectPostgresRequested += (_, _) => OnConnectPostgresClick(this, new RoutedEventArgs());
    }

    // ── Стартовый раздел ─────────────────────────────────────────────────────
    //
    // Какой раздел навигации (ActivityBar) открывается при запуске. Применяется при
    // следующем запуске — MainWindow читает AppSettings.LoadStartupModule() на старте.
    private void SelectStartupModule(string module)
    {
        foreach (var obj in StartupModuleCombo.Items)
            if (obj is ComboBoxItem { Tag: string tag } item && tag == module)
            {
                StartupModuleCombo.SelectedItem = item;
                return;
            }
    }

    private void OnStartupModuleChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (StartupModuleCombo.SelectedItem is ComboBoxItem { Tag: string module })
            AppSettings.SaveStartupModule(module);
    }

    // ── Стартовый вид календаря ──────────────────────────────────────────────
    //
    // Какой вид (Расписание/Журнал/День/Неделя/Месяц/Год) открывается при переходе в
    // Календарь. Индекс совпадает с порядком пунктов и ViewSelector навбара (0..5).
    // Применяется при следующем запуске — календарь читает AppSettings.LoadCalendarView().
    private void OnCalendarViewChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CalendarViewCombo.SelectedIndex >= 0)
            AppSettings.SaveCalendarView(CalendarViewCombo.SelectedIndex);
    }

    // ── Стартовый вид конструктора ───────────────────────────────────────────
    //
    // Вид коллекции (Таблица/Список) для коллекций без своего сохранённого режима.
    // Применяется при следующем открытии коллекции (репозиторий читает его как дефолт).
    private void OnConstructorViewChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ConstructorViewCombo.SelectedItem is ComboBoxItem { Tag: string mode })
            AppSettings.SaveConstructorView(mode);
    }

    // ── О программе ──────────────────────────────────────────────────────────
    //
    // Дата сборки = время последней записи бинаря на диск (детерминированные сборки
    // обнуляют PE-таймстамп, поэтому берём mtime файла). Environment.ProcessPath —
    // путь к запущенному .exe; работает одинаково в обычной и single-file-сборке
    // (в отличие от Assembly.Location, который в single-file пуст и даёт warning IL3000).
    // Так дата всегда отражает текущий бинарь и видна в раздаваемой версии — без ручного указания.
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
    // делегирует системе: open (macOS), explorer (Windows), xdg-open (Linux) — надёжно
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
        catch { /* нет обработчика для папки — молча игнорируем */ }
    }

    // ── Обновления ───────────────────────────────────────────────────────────
    //
    // Этап 1–2 автообновления: путь к папке с новой версией хранится в app.db, кнопка
    // «Проверить» читает версию удалённого SmartBase.exe (FileVersionInfo, без скачивания)
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

    // Тихая проверка при запуске (тумблер «Автообновление», вызывает MainWindow.Opened):
    // путь пуст/папка недоступна/версия актуальна — никаких окон, приложение стартует как
    // обычно (статус всё же пишем — виден, если открыть настройки); есть обновление — та же
    // модалка «Обновить/Отмена» и штатный поток загрузки/самозамены, что и по кнопке.
    public async Task CheckForUpdatesOnStartupAsync()
    {
        var folder = AppSettings.LoadUpdatePath().Trim();
        if (folder.Length == 0) return;

        var info = await Task.Run(() => CheckRemoteVersion(folder));
        SetUpdateStatus(info.Message, isError: info.Result == UpdateCheck.Error);
        if (info.Result == UpdateCheck.Available && info.RemoteExe is not null && info.Remote is not null)
            await StartUpdateAsync(info.RemoteExe, info.Remote);
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
            // вернётся ошибка с красным уведомлением (как «файл недоступен» в «Управлении БД»).
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
            // установщик, и как обновление); если его нет — берём обычный SmartBase. На macOS
            // артефакт — это .app-бандл (папка), а сравниваемый по дате файл — внутренний бинарь.
            if (!ResolveRemote(folder, out var remoteArtifact, out var remoteTimeFile, out var missing))
                return new(UpdateCheck.Error, missing);

            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            var cur     = Normalize(current);   // Major.Minor.Build — как версия в «О программе»

            // Версию читаем из удалённого файла (на Windows — из PE-ресурса .exe). Если версия
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

            // Версия недоступна или равна → ловим изменённый файл по СОДЕРЖИМОМУ, чтобы можно
            // было раздавать сборки без смены версии:
            //   • другой размер = точно другая сборка (дёшево, только метаданные, надёжнее даты —
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
                    return new(UpdateCheck.Available, $"Доступна новая сборка (файл изменён, {cur}).", remoteArtifact, cur);
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

        // Загрузка идёт в отдельном ОКНЕ с процентами (единый ProgressDialog, как импорт),
        // а не инлайн-полосой в настройках. Окно нельзя закрыть, пока копирование не кончится.
        var progressDlg = new ProgressDialog { Title = "Обновление" };
        progressDlg.SetProgress(0, "Загрузка обновления…");
        var dlgClosed = progressDlg.ShowDialog<object?>(win);
        try
        {
            var progress = new Progress<double>(p => progressDlg.SetProgress(p, "Загрузка обновления…"));
            if (!await DownloadAsync(remoteExe, tempExe, progress))
            {
                SetUpdateStatus("Загрузка прервалась — артефакт скопирован не полностью. Попробуйте ещё раз.", isError: true);
                return;
            }
            // На Unix temp-копию нужно сделать запускаемой (на macOS — внутренний бинарь бандла).
            Setup.InstallService.MakeExecutable(
                Setup.InstallService.IsMac ? Setup.InstallService.MacInnerBinary(tempExe) : tempExe);
        }
        catch (Exception ex)
        {
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
        // Windows — версия из PE: не даём «обновиться» на файл СТАРЕЕ текущего (файл в папке мог
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
        }
        catch (Exception ex)
        {
            SetUpdateStatus($"Не удалось запустить обновление: {ex.Message}", isError: true);
            return;
        }

        // Закрываем приложение: апдейтер дождётся выхода, заменит артефакт и перезапустит.
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
    // из-под курсора) — только двигаем пузырёк; применение — в EndScaleDrag.
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

    // Движение по слайдеру: при перетаскивании пузырёк следует за ползунком, при
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
        // Ползунок масштабируется LayoutTransform'ом окна, а попап живёт в немасштабированном
        // оверлее → смещение домножаем на текущий масштаб, иначе плашка «уезжает» от ползунка.
        ScaleValuePopup.HorizontalOffset = (frac * (w - thumbW) + thumbW / 2 - w / 2) * Scaling.Current;
    }

    // Значение слайдера в точке под курсором: обратная формула к смещению пузырька
    // (трек = ширина минус ползунок), с защёлкиванием на шаг слайдера.
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
    // НЕ применяем (перекладка интерфейса дёргает ползунок) — только пузырёк;
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

    // Пузырёк со значением показываем при наведении/перетаскивании ползунка.
    private void OnFontSliderEntered(object? sender, PointerEventArgs e)
    {
        UpdateFontBadge();
        FontValuePopup.IsOpen = true;
    }

    private void OnFontSliderExited(object? sender, PointerEventArgs e) => FontValuePopup.IsOpen = false;

    // Движение по слайдеру: при перетаскивании пузырёк следует за ползунком, при
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
        // Тот же учёт масштаба окна, что и у плашки масштабирования (попап — в оверлее).
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
    // + статус фоновой очереди монтирования для ещё не подключённых.
    public void Initialize(DatabaseRegistry registry, Func<int, bool> isMounted,
                           Func<int, (string Text, bool Failed)?>? queueStatus = null)
    {
        _registry    = registry;
        _isMounted   = isMounted;
        _queueStatus = queueStatus;
        RefreshDatabaseList();
    }

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

    // Переключение раздела: заголовок + видимость соответствующей панели.
    public void ShowCategory(string key)
    {
        AppearancePanel.IsVisible = key == "appearance";
        DatabasePanel.IsVisible   = key == "database";
        AboutPanel.IsVisible      = key == "about";

        // Заголовок берём из реестра по ключу — единственный источник подписей разделов.
        HeaderBar.Title = (SettingsRegistry.FindByKey(key) ?? SettingsRegistry.All[0]).Label;
        // Кнопка «Добавить базу данных» в навбаре осмысленна только в разделе БД.
        HeaderBar.ShowDatabaseActions = key == "database";
    }

    // ── Управление базой данных ──────────────────────────────────────────────

    private void RefreshDatabaseList()
    {
        if (_registry is null) { DatabaseList.ItemsSource = null; return; }

        DatabaseList.ItemsSource = _registry.GetAll().Select(d =>
        {
            var mounted = _isMounted?.Invoke(d.Id) ?? true;
            // Не смонтирована: либо ждёт/монтируется в фоновой очереди (живой статус),
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
                StatusText    = mounted ? null : queue?.Text
                    ?? (d.IsPostgres ? "Не подключена (сервер недоступен)"
                                     : "Не подключена (файл недоступен)"),
                StatusIsError = !mounted && (queue is null || queue.Value.Failed),
            };
        }).ToList();
    }

    // Создать новую БД: собственное русское окно (имя + папка) вместо системного
    // save-диалога — тот говорит на языке ОС и предлагает ПЕРЕЗАПИСАТЬ существующий
    // файл; здесь занятое имя даёт ошибку «уже существует», перезаписи нет в принципе.
    // Схема создаётся при монтировании.
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

        // Уже в реестре — не создаём дубль и честно говорим об этом (раньше клик
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

    private async void OnRemoveDatabaseClick(object? sender, RoutedEventArgs e)
    {
        if (_registry is null) return;
        if (sender is not Button { Tag: int id }) return;

        var entry = _registry.GetAll().FirstOrDefault(d => d.Id == id);
        if (entry is null || entry.IsDefault) return;   // дефолт не отключаем

        if (VisualRoot is not Window win) return;
        var dlg = new ConfirmDialog(entry.IsPostgres
            ? $"Отключить базу данных «{entry.Name}»? Данные на сервере не удаляются — её можно подключить снова."
            : $"Отключить базу данных «{entry.Name}»? Файл на диске не удаляется — её можно подключить снова.");
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
