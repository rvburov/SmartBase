using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SmartBase.Core;
using SmartBase.Settings;

namespace SmartBase.Controls.Components;

public partial class ActivityBar : UserControl
{
    public event EventHandler<bool>? SidebarToggled;
    public event EventHandler<string>? ModuleChanged;

    public ActivityBar()
    {
        InitializeComponent();

        WorkBtn.Click += (_, _) => BackgroundJobsPanel.Show(this, WorkBtn);

        // Значок фоновых работ живет отдельно от разделов: он показывает не «где я», а «что
        // сейчас делает программа». Реестр отчитывается с любого потока — маршалим сами.
        BackgroundJobs.Changed += OnBackgroundJobsChanged;
        UpdateWorkButton();

        SidebarBtn.IsCheckedChanged += (_, _) =>
        {
            UpdateSidebarIcon();
            SidebarToggled?.Invoke(this, SidebarBtn.IsChecked == true);
        };

        CalendarBtn.Click += (_, _) => SwitchModule("calendar");
        TasksBtn.Click += (_, _) => SwitchModule("tasks");
        ConstructorBtn.Click += (_, _) => SwitchModule("constructor");
        NotesBtn.Click += (_, _) => SwitchModule("notes");
        LibraryBtn.Click += (_, _) => SwitchModule("library");
        ExtensionsBtn.Click += (_, _) => SwitchModule("extensions");
        ProfileBtn.Click += (_, _) => SwitchModule("profile");
        SettingsBtn.Click += (_, _) => SwitchModule("settings");

        ApplyModuleVisibility();
    }

    // ── Значок фоновых работ ──────────────────────────────────────────────────

    private void OnBackgroundJobsChanged() => Dispatcher.UIThread.Post(UpdateWorkButton);

    /// <summary>
    /// Показать или спрятать значок и перерисовать кольцо. Дуга рисуется штриховкой круга:
    /// StrokeDashArray в Avalonia задается в ТОЛЩИНАХ обводки, поэтому длина окружности
    /// переводится в них делением на толщину. Отдельной фигуры-дуги нет намеренно —
    /// пересчитывать геометрию на каждый процент дороже, чем поменять два числа.
    /// </summary>
    private void UpdateWorkButton()
    {
        var any = BackgroundJobs.Any;
        WorkBtn.IsVisible = any;
        if (!any) { BackgroundJobsPanel.Close(); return; }

        var failed = BackgroundJobs.AnyFailed;
        WorkFailedDot.IsVisible = failed;
        // TryFindResource с запасным значением, а не FindResource: метод зовется и из
        // конструктора, когда панель еще не в дереве, — там поиск вернул бы UnsetValue.
        WorkArc.Stroke = this.TryFindResource(failed ? "BrushRed" : "BrushAccent", out var brush)
                         && brush is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(failed ? "#FF3B30" : "#007AFF"));

        const double Radius    = 14.5;   // половина стороны 29 из разметки
        const double Thickness = 2;
        var units = 2 * Math.PI * Radius / Thickness;

        // Объем неизвестен — кольцо полное и приглушенное: врать долей нельзя, а показать,
        // что работа идет, надо.
        var part = BackgroundJobs.Progress() ?? 1.0;
        part = Math.Clamp(part, 0, 1);

        WorkArc.StrokeDashArray = [units * part, units * (1 - part)];
    }

    // Разделы, скрытые в «Оформление → Отображение раздела навигации», убираем из панели.
    // «Настройки» и «Профиль» здесь не участвуют: их нет в ViewCatalog.Navigation, скрыть
    // настройки значило бы отрезать путь обратно.
    private void ApplyModuleVisibility()
    {
        var visible = ViewCatalog.Visible(ViewCatalog.ScopeNavigation)
                                 .Select(v => v.Key)
                                 .ToHashSet(StringComparer.Ordinal);

        CalendarBtn.IsVisible    = visible.Contains("calendar");
        TasksBtn.IsVisible       = visible.Contains("tasks");
        ConstructorBtn.IsVisible = visible.Contains("constructor");
        NotesBtn.IsVisible       = visible.Contains("notes");
        LibraryBtn.IsVisible     = visible.Contains("library");
        ExtensionsBtn.IsVisible  = visible.Contains("extensions");
    }

    public void SetSidebarOpen(bool isOpen)
    {
        SidebarBtn.IsChecked = isOpen;
        UpdateSidebarIcon();
    }

    // Открыт → panel-right-open (←, свернуть); закрыт → panel-left-open (→, развернуть)
    private void UpdateSidebarIcon()
    {
        var open = SidebarBtn.IsChecked == true;
        PanelOpenIcon.IsVisible  = !open;
        PanelCloseIcon.IsVisible = open;
        ToolTip.SetTip(SidebarBtn, open ? "Свернуть боковую панель" : "Развернуть боковую панель");
    }

    public void SetModuleActive(string module)
    {
        CalendarBtn.IsChecked = module == "calendar";
        TasksBtn.IsChecked = module == "tasks";
        ConstructorBtn.IsChecked = module == "constructor";
        NotesBtn.IsChecked = module == "notes";
        LibraryBtn.IsChecked = module == "library";
        ExtensionsBtn.IsChecked = module == "extensions";
        ProfileBtn.IsChecked = module == "profile";
        SettingsBtn.IsChecked = module == "settings";
    }

    private void SwitchModule(string module)
    {
        SetModuleActive(module);
        ModuleChanged?.Invoke(this, module);
    }
}
