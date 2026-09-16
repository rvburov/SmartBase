using Avalonia.Controls;
using Avalonia.Input;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Окно токена робота из менеджера паролей (шестеренка в строке токена): что это за токен,
/// тумблер «Включен» и «Отозвать».
///
/// <b>Почему здесь пояснение, а в строке нет.</b> Строка списка отвечает на один вопрос — «что
/// заведено и живо ли оно»; когда токен выдан и стучался ли отправитель, спрашивают, уже решая,
/// что с токеном делать. Это и есть момент открытия окна.
///
/// Окно само ничего не меняет: включение и отзыв оно отдает вызывающему, у которого есть реестр и
/// проверка прав, и получает обратно ответ «сделано или нет». Создание из окна означало бы, что
/// реестр знают двое.
/// </summary>
public partial class TokenSettingsDialog : Window
{
    private readonly Func<Window, bool, bool> _enable;
    private readonly Func<Window, Task<bool>> _revoke;

    // Что сейчас на самом деле записано. Тумблер сверяется с этим, а не с флагом подавления:
    // и программная расстановка, и откат после отказа приходят тем же событием.
    private bool _enabled;

    private bool _revoking;

    /// <param name="name">Имя токена — оно же имя робота в истории изменений.</param>
    /// <param name="database">База, в которую токену разрешено писать.</param>
    /// <param name="created">Когда выдан; <c>null</c> — не записано.</param>
    /// <param name="used">Последнее обращение; <c>null</c> — обращений не было.</param>
    /// <param name="enabled">Включен ли токен сейчас.</param>
    /// <param name="enable">Включить или выключить; ложь — не вышло (нет права), и тумблер
    /// возвращается.</param>
    /// <param name="revoke">Отозвать после подтверждения; истина — отозван, окно закрывается.</param>
    public TokenSettingsDialog(string name, string database,
                               DateTimeOffset? created, DateTimeOffset? used, bool enabled,
                               Func<Window, bool, bool> enable,
                               Func<Window, Task<bool>> revoke)
    {
        InitializeComponent();

        _enable  = enable;
        _revoke  = revoke;
        _enabled = enabled;

        Title         = $"Токен «{name}»";
        InfoText.Text = Explain(name, database, created, used);

        EnabledToggle.IsChecked         = enabled;
        EnabledToggle.IsCheckedChanged += (_, _) => Toggle();

        RevokeBtn.Click += async (_, _) => await RevokeAsync();
        CloseBtn.Click  += (_, _) => Close();
        KeyDown         += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// <summary>Пояснение о токене: робот, база, выдача, обращения и почему его не показать.</summary>
    internal static string Explain(string name, string database,
                                   DateTimeOffset? created, DateTimeOffset? used) =>
        $"Токен робота «{name}»: им чужая программа пишет в базу «{database}» как "
        + "пользователь-робот со своими правами. "
        + (created is { } issued
            ? $"Выдан {issued.ToLocalTime():dd.MM.yyyy HH:mm}. "
            : "Когда выдан, не записано. ")
        + (used is { } last
            ? $"Последнее обращение — {last.ToLocalTime():dd.MM.yyyy HH:mm}. "
            : "Обращений с ним еще не было. ")
        + "Программа хранит только проверочный отпечаток токена, поэтому показать его нельзя: "
        + "забыли — отзовите и выдайте новый.";

    private void Toggle()
    {
        var on = EnabledToggle.IsChecked == true;
        if (on == _enabled) return;

        if (_enable(this, on))
        {
            _enabled = on;
            return;
        }

        // Не вышло — тумблер обратно: показывать включенным то, что не включилось, нельзя.
        EnabledToggle.IsChecked = _enabled;
    }

    private async Task RevokeAsync()
    {
        if (_revoking) return;
        _revoking = true;

        try
        {
            if (await _revoke(this)) Close();
        }
        finally { _revoking = false; }
    }
}
