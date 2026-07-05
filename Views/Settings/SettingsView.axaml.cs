using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DataBase.Controls.Dialogs;
using DataBase.Database;
using DataBase.Database.Entities;
using DataBase.Settings;
using DataBase.Utils;

namespace DataBase.Views.Settings;

public partial class SettingsView : UserControl
{
    private DatabaseRegistry? _registry;
    // Признак «БД сейчас смонтирована» (даёт MainWindow по рабочему пространству).
    private Func<int, bool>?  _isMounted;

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

        // Слайдер размера шрифта отражает текущую дельту (стандарт = 0, диапазон -3…+3).
        FontSizeSlider.Value = FontScale.Delta;

        // Слайдер масштаба отражает текущий уровень в процентах (стандарт = 100, диапазон 70…130).
        ScaleSlider.Value = Scaling.Current * 100;

        // Стартовый раздел: выделить сохранённый пункт (по Tag).
        SelectStartupModule(AppSettings.LoadStartupModule());

        // Стартовый вид календаря: выделить по индексу (Tag = "0".."5").
        CalendarViewCombo.SelectedIndex = AppSettings.LoadCalendarView();

        // Стартовый вид конструктора: Таблица (0) или Список (1).
        ConstructorViewCombo.SelectedIndex = AppSettings.LoadConstructorView() == "list" ? 1 : 0;
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
    // «Проверить» читает версию удалённого DataBase.exe (FileVersionInfo, без скачивания)
    // и сравнивает с текущей. Скачивание и замену .exe выполнит отдельный DataBase-Setup.exe
    // (следующие этапы — см. docs/guides/auto-update.md).

    private enum UpdateCheck { Available, UpToDate, Error }

    // Результат проверки: тип, сообщение и (если есть обновление) путь к файлу и его версия.
    private sealed record UpdateInfo(UpdateCheck Result, string Message,
                                     string? RemoteExe = null, Version? Remote = null);

    // Автосохранение пути при потере фокуса полем.
    private void OnUpdatePathChanged(object? sender, RoutedEventArgs e) =>
        AppSettings.SaveUpdatePath(UpdatePathBox.Text ?? "");

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

            // Источник обновления — DataBase-Setup (один артефакт на сервере работает и как
            // установщик, и как обновление); если его нет — берём обычный DataBase. На macOS
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

            // Версия недоступна или равна → дата изменения файла: новее на сервере = новая
            // сборка (с допуском 2 с против округления меток времени).
            var localExe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(localExe) && System.IO.File.Exists(localExe))
            {
                var localTime  = System.IO.File.GetLastWriteTimeUtc(localExe);
                var remoteTime = System.IO.File.GetLastWriteTimeUtc(remoteTimeFile);
                if (remoteTime > localTime.AddSeconds(2))
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

        var dlg = new ConfirmDialog(
            $"Доступна версия {remote}. Приложение закроется, обновится и снова откроется.",
            "Обновить");
        if (await dlg.ShowDialog<bool>(win) != true) return;

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
    // Масштаб применяется ко всему окну вживую: MainWindow слушает Scaling.ScaleChanged
    // и ставит LayoutTransform на корень. Слайдер — в процентах, Scaling хранит множитель.
    private void OnScaleChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        Scaling.SetScale(Math.Round(e.NewValue) / 100.0);
        UpdateScaleBadge();
    }

    private void OnScaleSliderEntered(object? sender, PointerEventArgs e)
    {
        UpdateScaleBadge();
        ScaleValuePopup.IsOpen = true;
    }

    private void OnScaleSliderExited(object? sender, PointerEventArgs e) => ScaleValuePopup.IsOpen = false;

    // Текст = текущий масштаб в процентах; смещение пузырька — под текущий ползунок.
    private void UpdateScaleBadge()
    {
        ScaleValueText.Text = $"{(int)Math.Round(ScaleSlider.Value)} %";

        var range = ScaleSlider.Maximum - ScaleSlider.Minimum;
        var w     = ScaleSlider.Bounds.Width;
        if (range <= 0 || w <= 0) return;

        var frac = (ScaleSlider.Value - ScaleSlider.Minimum) / range;
        const double thumbW = 26;
        // Ползунок масштабируется LayoutTransform'ом окна, а попап живёт в немасштабированном
        // оверлее → смещение домножаем на текущий масштаб, иначе плашка «уезжает» от ползунка.
        ScaleValuePopup.HorizontalOffset = (frac * (w - thumbW) + thumbW / 2 - w / 2) * Scaling.Current;
    }

    // Кнопка-стрелка: подтверждение в стандартном диалоге → возврат к 100 %.
    private async void OnScaleReset(object? sender, RoutedEventArgs e)
    {
        if (VisualRoot is not Window win) return;
        var dlg = new ConfirmDialog("Вернуть уровень масштабирования по умолчанию?", "Сбросить");
        if (await dlg.ShowDialog<bool>(win) != true) return;

        ScaleSlider.Value = AppSettings.ScaleDefault * 100;   // → OnScaleChanged → SetScale(1.0)
    }

    // Размер шрифта меняется вживую: дельта пишется в Application.Resources (DynamicResource),
    // C#-виды пересобираются по событию FontScale.Changed.
    private void OnFontSizeChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        FontScale.SetDelta((int)Math.Round(e.NewValue));
        UpdateFontBadge();
    }

    // Пузырёк со значением показываем при наведении/перетаскивании ползунка.
    private void OnFontSliderEntered(object? sender, PointerEventArgs e)
    {
        UpdateFontBadge();
        FontValuePopup.IsOpen = true;
    }

    private void OnFontSliderExited(object? sender, PointerEventArgs e) => FontValuePopup.IsOpen = false;

    // Текст = итоговый базовый размер (стандарт ± дельта); смещение пузырька — под текущий ползунок.
    private void UpdateFontBadge()
    {
        var delta     = (int)Math.Round(FontSizeSlider.Value);
        var effective = FontScale.BaseFontSize + delta;            // итоговый размер шрифта
        FontValueText.Text = ((int)Math.Round(effective)).ToString();

        // Квадрат: сторона растёт/уменьшается вместе с размером шрифта.
        var side = Math.Round(effective * 2.0);
        FontBadgeBorder.Width  = side;
        FontBadgeBorder.Height = side;

        var range = FontSizeSlider.Maximum - FontSizeSlider.Minimum;
        var w     = FontSizeSlider.Bounds.Width;
        if (range <= 0 || w <= 0) return;

        var frac = (FontSizeSlider.Value - FontSizeSlider.Minimum) / range;
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

    // Вызывается из MainWindow: реестр + способ узнать, смонтирована ли БД сейчас.
    public void Initialize(DatabaseRegistry registry, Func<int, bool> isMounted)
    {
        _registry  = registry;
        _isMounted = isMounted;
        RefreshDatabaseList();
    }

    // Перестроить список (статус монтирования мог измениться). Вызывается из MainWindow.
    public void RefreshDatabases() => RefreshDatabaseList();

    // Переключение раздела: заголовок + видимость соответствующей панели.
    public void ShowCategory(string key)
    {
        AppearancePanel.IsVisible = key == "appearance";
        DatabasePanel.IsVisible   = key == "database";
        AboutPanel.IsVisible      = key == "about";

        // Заголовок берём из реестра по ключу — единственный источник подписей разделов.
        HeaderBar.Title = (SettingsRegistry.FindByKey(key) ?? SettingsRegistry.All[0]).Label;
    }

    // ── Управление базой данных ──────────────────────────────────────────────

    private void RefreshDatabaseList()
    {
        if (_registry is null) { DatabaseList.ItemsSource = null; return; }

        DatabaseList.ItemsSource = _registry.GetAll().Select(d => new DatabaseRow
        {
            Id        = d.Id,
            Name      = d.Name,
            Path      = d.Path,
            IsDefault = d.IsDefault,
            IsMounted = _isMounted?.Invoke(d.Id) ?? true,
        }).ToList();
    }

    // Создать новую БД: выбрать имя/расположение файла (схема создаётся при монтировании).
    private async void OnCreateDatabaseClick(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (_registry is null || top is null) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Создать базу данных",
            SuggestedFileName = "database",
            DefaultExtension  = "db",
            FileTypeChoices   = [new FilePickerFileType("База данных SQLite") { Patterns = ["*.db"] }],
        });
        if (file is null) return;

        RegisterDatabase(file.Path.LocalPath);
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

        RegisterDatabase(file.Path.LocalPath);
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

    private async void OnRemoveDatabaseClick(object? sender, RoutedEventArgs e)
    {
        if (_registry is null) return;
        if (sender is not Button { Tag: int id }) return;

        var entry = _registry.GetAll().FirstOrDefault(d => d.Id == id);
        if (entry is null || entry.IsDefault) return;   // дефолт не отключаем

        if (VisualRoot is not Window win) return;
        var dlg = new ConfirmDialog(
            $"Отключить базу данных «{entry.Name}»? Файл на диске не удаляется — её можно подключить снова.");
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
