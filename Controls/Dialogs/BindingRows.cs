using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Строка раскладки «колонка ответа → место в коллекции» (окно «В коллекцию»).
///
/// <b>Состояние живет в модели, а не в контроле, и это не стиль, а требование.</b> Список
/// виртуализуется: колонок в ответе бывает под сотню, и комбобоксы, созданные разом, видно и
/// на открытии окна, и на прокрутке. Виртуализация пересоздает контролы при прокрутке —
/// выбранное в комбобоксе, живи оно в самом комбобоксе, терялось бы, стоило строке уехать за
/// край экрана.
/// </summary>
/// <param name="column">Имя колонки в ответе.</param>
/// <param name="options">Куда ее можно положить — подписи мест в порядке списка.</param>
/// <param name="selected">Что выбрано сейчас.</param>
/// <param name="sample">Настоящие значения этой колонки из первых строк ответа — через запятую;
/// пусто — значений нет. Раздел 2 обещает «показ первых 20 строк», и обещает не ради полноты:
/// раскладка подбирается ПО ИМЕНАМ, а у сторонних программ имена похожи до неразличимости
/// (<c>name</c> и <c>title</c>, <c>sum</c> и <c>total</c>). Ошибку в них без значений видно
/// только после укладки — то есть по испорченной коллекции.</param>
public sealed class BindingMapRow(string column, IReadOnlyList<string> options, int selected,
                                  string sample = "")
    : INotifyPropertyChanged
{
    private int _selected = selected;

    /// <summary>Имя колонки — левая половина строки.</summary>
    public string Column { get; } = column;

    /// <summary>Настоящие значения колонки из ответа; пусто — показывать нечего.</summary>
    public string Sample { get; } = sample;

    /// <summary>Есть ли что показывать: пустая строка образцов не должна занимать место.</summary>
    public bool HasSample => Sample.Length > 0;

    /// <summary>Подписи мест для выпадающего списка.</summary>
    public IReadOnlyList<string> Options { get; } = options;

    /// <summary>Что выбрано; смена сразу сообщается окну — оно пересчитывает список ключей.</summary>
    public int Selected
    {
        get => _selected;
        set
        {
            // Виртуализация отвязывает шаблон от старой модели, выставляя SelectedIndex в −1:
            // принять это значило бы «человек снял выбор», и уехавшая за край строка теряла бы
            // настроенное.
            if (value < 0 || value == _selected) return;

            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            Picked?.Invoke();
        }
    }

    /// <summary>Кого позвать, когда человек поменял место. Ставит окно.</summary>
    public Action? Picked { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Строка исходящей привязки «свойство коллекции → уходит ли и под каким именем» (окно «Что
/// уходит наружу»).
///
/// Модель та же по назначению, что <see cref="BindingMapRow"/>, и по той же причине: свойств в
/// коллекции бывают сотни, список виртуализуется, а галочка и имя обязаны пережить прокрутку.
/// </summary>
/// <param name="fieldId">Ключ свойства; 0 — название записи.</param>
/// <param name="title">Имя свойства, как его видит человек.</param>
public sealed class OutgoingFieldRow(int fieldId, string title) : INotifyPropertyChanged
{
    private bool   _takes;
    private string _alias = "";

    public int    FieldId { get; } = fieldId;
    public string Title   { get; } = title;

    /// <summary>Уходит ли свойство наружу.</summary>
    public bool Takes
    {
        get => _takes;
        set
        {
            if (value == _takes) return;

            _takes = value;
            Changed();
            Picked?.Invoke();
        }
    }

    /// <summary>Под каким именем его ждет сторонняя программа; пусто — под своим.</summary>
    public string Alias
    {
        get => _alias;
        set
        {
            var text = value ?? "";
            if (text == _alias) return;

            _alias = text;
            Changed();
            Picked?.Invoke();
        }
    }

    /// <summary>Кого позвать, когда человек поменял строку. Ставит окно.</summary>
    public Action? Picked { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed([CallerMemberName] string name = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Значения колонок ответа для окна раскладки: по одной готовой строке на колонку.
///
/// Вынесено из окна ОТДЕЛЬНО ради проверяемости: это единственное место, где решается, что
/// человек увидит под именем колонки, а проверить его внутри окна — значит поднимать Avalonia
/// ради сборки строки.
/// </summary>
public static class BindingSample
{
    /// <summary>
    /// Значения каждой колонки одной строкой: «А-1, А-2, А-3 …».
    ///
    /// <b>Сколько строк показывать.</b> Раздел 2 называет двадцать, и берутся именно они — но не
    /// все двадцать попадают на экран: под именем колонки помещается одна строка текста, и она
    /// обрезается. Это не половинчатость: двадцать значений читают не целиком, их пробегают
    /// глазами, чтобы отличить номера от названий. Двадцать строк таблицей заняли бы окно, в
    /// котором человек пришел выбирать места колонок.
    ///
    /// <b>Пустые значения пропускаются.</b> Колонка, у которой первые три значения пусты, а
    /// четвертое «А-1», — самый интересный случай для сверки, и показать по ней «, , ,» значило
    /// бы промолчать ровно там, где ответ нужен.
    ///
    /// Значения берутся КАК ЕСТЬ и никуда не уходят: окно показывает их человеку, который и так
    /// смотрит на эти данные, — ни в журнал, ни в буфер, ни в файл они отсюда не попадают.
    /// </summary>
    public static IReadOnlyList<string> Of(IReadOnlyList<string> columns,
                                          IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var made = new List<string>(columns.Count);

        for (var column = 0; column < columns.Count; column++)
        {
            var values = new List<string>();

            foreach (var row in rows)
            {
                if (column >= row.Count) continue;

                var value = (row[column] ?? "").Trim();
                if (value.Length == 0) continue;

                // Длинное значение обрезается здесь, а не разметкой: одно описание на тысячу
                // знаков вытеснило бы из строки все остальные, и сверять стало бы нечего.
                values.Add(value.Length > 40 ? value[..40] + "…" : value);

                if (values.Count >= 20) break;
            }

            made.Add(values.Count == 0 ? "" : string.Join(", ", values));
        }

        return made;
    }
}
