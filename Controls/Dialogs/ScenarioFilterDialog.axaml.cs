using Avalonia.Controls;
using SmartBase.Integrations;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Окно правки шага «отбор строк» — то самое «ЕСЛИ» из плана (docs/ROADMAP-API.md, раздел 2).
///
/// В плане условия нарисованы отдельной полосой карточки сценария, между «когда» и «что делать».
/// Здесь они стали ШАГОМ, и причина не в удобстве: условию нужны данные, а данные появляются
/// только после того, как их кто-то принес. Полоса в шапке карточки обязана была бы угадывать, к
/// какому шагу относится, — а поставленный между «разобрать сообщение» и «зарегистрировать»,
/// отбор не гадает ни о чем.
///
/// Имя колонки НАБИРАЕТСЯ, а не выбирается из списка: какие колонки принесет предыдущий шаг,
/// заранее не знает никто — ответ чужой программы меняется без предупреждения. Тот же прием, что
/// и у подстановок <c>{{колонка}}</c> в шаге-действии; ошибка в имени останавливает шаг словами.
/// Там, где колонки известны (исходящая привязка — свойства своей коллекции), тот же виджет
/// показывает выбор: <see cref="Widgets.ConditionRows"/>.
///
/// Возвращает <see cref="ScenarioStep"/> или <c>null</c> при отмене.
/// </summary>
public partial class ScenarioFilterDialog : Window
{
    public ScenarioFilterDialog() : this(ScenarioStep.OfFilter()) { }

    /// <param name="step">Что правим; шаг без условий — новый отбор.</param>
    public ScenarioFilterDialog(ScenarioStep step)
    {
        InitializeComponent();

        TestsPanel.Load(step.Tests);

        AddTestBtn.Click += (_, _) =>
        {
            if (TestsPanel.Count >= ScenarioStep.MaxConditions)
            {
                NoticeBanner.Show($"Условий в одном отборе не больше {ScenarioStep.MaxConditions}: "
                                + "отберите в два приема.");
                return;
            }

            TestsPanel.Add();
        };

        CancelBtn.Click += (_, _) => Close(null);
        SaveBtn.Click   += (_, _) => Save();
    }

    private void Save()
    {
        var step = ScenarioStep.OfFilter([.. TestsPanel.Read()]);

        var problems = step.Problems();
        if (problems.Count > 0)
        {
            NoticeBanner.Show(string.Join("; ", problems) + ".");
            return;
        }

        Close(step);
    }
}
