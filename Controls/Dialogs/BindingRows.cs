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
public sealed class BindingMapRow(string column, IReadOnlyList<string> options, int selected)
    : INotifyPropertyChanged
{
    private int _selected = selected;

    /// <summary>Имя колонки — левая половина строки.</summary>
    public string Column { get; } = column;

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
/// <param name="allowed">Можно ли его вообще отправлять: помеченное свойство выбрать нельзя.</param>
public sealed class OutgoingFieldRow(int fieldId, string title, bool allowed) : INotifyPropertyChanged
{
    private bool   _takes;
    private string _alias = "";

    public int    FieldId { get; } = fieldId;
    public string Title   { get; } = title;

    /// <summary>Доступна ли галочка. Метка конфиденциальности снимает ее совсем.</summary>
    public bool Allowed => allowed;

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

    /// <summary>Под каким именем его ждет чужая программа; пусто — под своим.</summary>
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

    /// <summary>Почему галочки нет — подпись рядом со строкой; пусто, если все в порядке.</summary>
    public string Note { get; init; } = "";

    /// <summary>Есть ли что показать в подписи — для видимости в разметке.</summary>
    public bool HasNote => Note.Length > 0;

    /// <summary>Кого позвать, когда человек поменял строку. Ставит окно.</summary>
    public Action? Picked { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed([CallerMemberName] string name = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
