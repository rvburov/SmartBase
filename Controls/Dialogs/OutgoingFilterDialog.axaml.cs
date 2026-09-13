using Avalonia.Controls;
using Avalonia.Input;
using SmartBase.Integrations;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Отбор записей у исходящей привязки — «какие записи уходят» (docs/ROADMAP-API.md, раздел 2:
/// «Наружу — зеркально: коллекция И ФИЛЬТР»).
///
/// <b>Почему отдельным окном.</b> Окно «Что уходит наружу» и так полно: база, коллекция, список
/// свойств с именами, форма тела, вид собранного тела. Список условий, вставленный между ними,
/// отодвинул бы главное — перечень уходящих полей, — а он и есть ответ на вопрос «что мы
/// отдаем». Здесь тот же прием, что у шагов сценария: карточка показывает итог строкой, правка
/// живет в своем окне.
///
/// <b>Колонки выбираются, а не набираются.</b> В отличие от шага «отбор», где колонки приносит
/// чужая выгрузка и знать их заранее нельзя, здесь коллекция наша: имена свойств известны, и
/// набирать их руками значило бы приглашать опечатку туда, где ее можно не допустить.
///
/// Возвращает список условий (пустой — «без отбора») либо <c>null</c> при отмене.
/// </summary>
public partial class OutgoingFilterDialog : Window
{
    public OutgoingFilterDialog() : this([], []) { }

    /// <param name="columns">Имена свойств коллекции, по которым можно отбирать.</param>
    /// <param name="saved">Прошлый отбор; пусто — собирается с нуля.</param>
    public OutgoingFilterDialog(IReadOnlyList<string> columns,
                                IReadOnlyList<ScenarioCondition> saved)
    {
        InitializeComponent();

        // atLeastOne: одна пустая строка показывается всегда — иначе окно открывается пустым и
        // непонятно, что в нем делать. Пустые строки при сохранении отбрасываются, поэтому
        // «открыл и закрыл» отбора не заводит.
        Rows.Load(saved, columns, columnHint: "Статус", valueHint: "Отгружен", atLeastOne: true);

        AddBtn.Click += (_, _) =>
        {
            if (Rows.Count >= OutgoingBinding.MaxConditions)
            {
                NoticeBanner.Show($"Условий в одном отборе не больше "
                                + $"{OutgoingBinding.MaxConditions}: столько правил читаются хуже, "
                                + "чем отдельная коллекция.");
                return;
            }

            Rows.Add();
        };

        // «Без отбора» — не то же самое, что «Отмена»: отбор был, и его снимают. Отдельная
        // кнопка нужна потому, что снять условия, стирая их по одному, нельзя — последняя
        // строка возвращается на место.
        ClearBtn.Click  += (_, _) => Close(Array.Empty<ScenarioCondition>());
        CancelBtn.Click += (_, _) => Close(null);
        SaveBtn.Click   += (_, _) => Save();

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(null); };
    }

    private void Save()
    {
        var tests = Rows.Read();

        // Проверяются условия по одному, а не привязка целиком: у привязки свои беды — не
        // выбрана коллекция, нет полей, — и отвечать за них этому окну нечем.
        var problems = tests.SelectMany(t => t.Problems().Select(p => $"«{t}»: {p}")).ToList();

        if (problems.Count > 0)
        {
            NoticeBanner.Show(string.Join("; ", problems) + ".");
            return;
        }

        Close(tests);
    }
}
