using Avalonia.Controls;
using SmartBase.Integrations;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Выдача токена доступа для входящего канала (docs/ROADMAP-API.md, 10.12).
///
/// Спрашивается ровно два: как назвать отправителя и в какую базу он пишет. Ни прав, ни срока,
/// ни адресов: права роботу выдает администратор базы обычной панелью допуска — той же, что и
/// человеку, — а заводить здесь вторую такую панель значило бы завести и второй набор правил.
///
/// Окно само ничего не создает: имя и базу оно отдает тому, кто умеет выдать токен, и получает
/// обратно причину отказа. Создание из окна означало бы, что реестр знают двое.
/// </summary>
public partial class TokenDialog : Window
{
    private readonly IReadOnlyList<(int DbId, string Name)> _databases;
    private readonly Func<string, int, string?>             _issue;

    public TokenDialog()
        : this([], (_, _) => "выдавать токен некуда") { }

    /// <param name="databases">Открытые базы: токен выдается под одну из них.</param>
    /// <param name="issue">Имя и база; возвращает причину отказа либо <c>null</c>.</param>
    public TokenDialog(IReadOnlyList<(int DbId, string Name)> databases,
                       Func<string, int, string?> issue)
    {
        InitializeComponent();

        _databases = databases;
        _issue     = issue;

        DbCombo.ItemsSource   = databases.Select(d => d.Name).ToList();
        DbCombo.SelectedIndex = databases.Count > 0 ? 0 : -1;

        CancelBtn.Click += (_, _) => Close(false);
        SaveBtn.Click   += (_, _) => Save();
    }

    private void Save()
    {
        var name = NameBox.Text ?? "";

        if (AccessToken.NameTrouble(name) is { Length: > 0 } bad)
        {
            NoticeBanner.Show(bad);
            return;
        }

        if (DbCombo.SelectedIndex < 0 || DbCombo.SelectedIndex >= _databases.Count)
        {
            NoticeBanner.Show("Выберите базу данных, в которую будет писать отправитель.");
            return;
        }

        if (_issue(name.Trim(), _databases[DbCombo.SelectedIndex].DbId) is { Length: > 0 } trouble)
        {
            NoticeBanner.Show(char.ToUpperInvariant(trouble[0]) + trouble[1..]);
            return;
        }

        Close(true);
    }
}
