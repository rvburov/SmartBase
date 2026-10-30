using Avalonia.Controls;
using Avalonia.Input;
using SmartBase.Integrations;

namespace SmartBase.Controls.Dialogs;

/// <summary>Запрос двоичного файла и выражение его имени; содержимое разбирает действие.</summary>
public partial class ScenarioDownloadDialog : Window
{
    private readonly IReadOnlyList<(int Id, string Name, bool Bound)> _requests;

    public ScenarioDownloadDialog() : this(ScenarioStep.OfDownload(0, ""), []) { }

    public ScenarioDownloadDialog(ScenarioStep step,
                                  IReadOnlyList<(int Id, string Name, bool Bound)> requests)
    {
        InitializeComponent();
        _requests = requests;
        RequestCombo.ItemsSource = requests.Select(request => request.Name).ToList();
        RequestCombo.SelectedIndex = requests.ToList().FindIndex(request => request.Id == step.RequestId);
        NameBox.Text = step.Text;
        NameBox.MaxLength = ScenarioStep.MaxTextLength;

        CancelBtn.Click += (_, _) => Close(null);
        SaveBtn.Click += (_, _) => Save();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(null); };
    }

    private void Save()
    {
        var index = RequestCombo.SelectedIndex;
        var requestId = index >= 0 && index < _requests.Count ? _requests[index].Id : 0;
        var step = ScenarioStep.OfDownload(requestId, (NameBox.Text ?? "").Trim());
        var problems = step.Problems();
        if (problems.Count > 0)
        {
            NoticeBanner.Show(string.Join("; ", problems) + ".");
            return;
        }
        Close(step);
    }
}
