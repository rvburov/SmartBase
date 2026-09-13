using Avalonia.Controls;
using Avalonia.Input;
using SmartBase.Integrations;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Окно настройки шага «экспорт выборки»: коллекция, формат, папка и отбор строк
/// (docs/ROADMAP-API.md, шаг 11).
///
/// Читает шаг КОЛЛЕКЦИЮ, а не поток предыдущих шагов: выборка существует и тогда, когда
/// сценарию никто ничего не принес, — «каждый вечер выгрузить неоплаченное». Папку выбирают
/// строкой, а не окном, по той же причине, что у папки обмена: путь на общем диске приходит
/// от администратора и вставляется целиком, а разрешает его политика, и проверка живет на
/// запуске.
///
/// Возвращает <see cref="ScenarioStep"/> или <c>null</c> при отмене.
/// </summary>
public partial class ScenarioExportDialog : Window
{
    /// <summary>Подписи форматов — по словарю модели, чтобы список нельзя было рассинхронизировать.</summary>
    private static readonly (string Format, string Text)[] Formats =
    [
        ("csv",  "CSV"),
        ("json", "JSON"),
        ("xlsx", "Excel"),
    ];

    private readonly IReadOnlyList<CollectionChoice> _places;

    public ScenarioExportDialog() : this(ScenarioStep.OfExport(0, 0, "", "csv"), []) { }

    /// <param name="step">Что правим; шаг с пустой коллекцией — новый.</param>
    /// <param name="places">Коллекции открытых баз.</param>
    public ScenarioExportDialog(ScenarioStep step, IReadOnlyList<CollectionChoice> places)
    {
        InitializeComponent();

        _places = places;

        PlaceCombo.ItemsSource = _places.Select(p => p.Name + " — " + p.Database).ToList();
        PlaceCombo.SelectedIndex = Math.Max(0, _places.ToList().FindIndex(
            p => p.DbId == step.DbId && p.CollectionId == step.CollectionId));

        FormatCombo.ItemsSource = Formats.Select(f => f.Text).ToList();
        FormatCombo.SelectedIndex = Math.Max(0, Array.FindIndex(
            Formats, f => f.Format == step.Format.Trim().ToLowerInvariant()));

        FolderBox.Text = step.Folder;

        // Колонки здесь СВОИ — выгружается коллекция, — но список свойств окно не знает: оно
        // получает только коллекции с именами. Поэтому имя набирается, как в шаге «отбор».
        TestsPanel.Load(step.Tests, columnHint: "Статус", valueHint: "Не оплачен",
                        atLeastOne: false, emptyNote: "Условий нет — уходит вся коллекция.");

        AddTestBtn.Click += (_, _) =>
        {
            if (TestsPanel.Count >= ScenarioStep.MaxConditions)
            {
                NoticeBanner.Show($"Условий не больше {ScenarioStep.MaxConditions}: "
                                + "выгрузите в два приема.");
                return;
            }

            TestsPanel.Add();
        };

        if (_places.Count == 0)
            NoticeBanner.Show("Нет открытых баз данных: выгружать нечего.");

        SaveBtn.IsEnabled = _places.Count > 0;

        CancelBtn.Click += (_, _) => Close(null);
        SaveBtn.Click   += (_, _) => Save();
        KeyDown         += (_, e) => { if (e.Key == Key.Escape) Close(null); };
    }

    // ── Сохранение ────────────────────────────────────────────────────────────

    private void Save()
    {
        if (PlaceCombo.SelectedIndex < 0 || PlaceCombo.SelectedIndex >= _places.Count)
        {
            NoticeBanner.Show("Выберите коллекцию для выгрузки.");
            return;
        }

        var place  = _places[PlaceCombo.SelectedIndex];
        var format = Formats[Math.Clamp(FormatCombo.SelectedIndex, 0, Formats.Length - 1)].Format;

        var step = ScenarioStep.OfExport(place.DbId, place.CollectionId,
                                         (FolderBox.Text ?? "").Trim(), format,
                                         [.. TestsPanel.Read()]);

        var problems = step.Problems();
        if (problems.Count > 0)
        {
            NoticeBanner.Show(string.Join("; ", problems) + ".");
            return;
        }

        Close(step);
    }
}
