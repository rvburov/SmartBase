using Avalonia.Controls;
using SmartBase.Integrations;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Подтверждение исходящего направления мастер-паролем (docs/ROADMAP-API.md, 10.8).
///
/// Окно само ничего не решает: пароль оно передает тому, кто умеет его проверить, — реестру,
/// который спрашивает политику. Проверка пароля на экране означала бы вторую ее реализацию, а
/// вторая реализация проверки полномочий рано или поздно расходится с первой в сторону
/// разрешения.
///
/// Ошибка НЕ закрывает окно: неверный пароль — обычная опечатка, и заставлять человека проходить
/// путь заново из-за нее незачем. Закрывается оно только по удаче или по «Отмене».
/// </summary>
public partial class DirectionConfirmDialog : Window
{
    /// <summary>
    /// Готовые сроки. Набираться числом тут нечему: «на сколько дней» — это вопрос с четырьмя
    /// разумными ответами, а «на 1000 дней» означает «навсегда», чего подтверждение не значит.
    /// </summary>
    private static readonly (int Days, string Text)[] Terms =
    [
        (30,  "30 дней"),
        (90,  "90 дней"),
        (180, "полгода"),
        (365, "год"),
    ];

    private readonly Func<string, int, string?> _confirm;

    public DirectionConfirmDialog()
        : this("", (_, _) => "подтверждать нечего") { }

    /// <param name="what">Что подтверждаем — имя запроса и метод с путем.</param>
    /// <param name="confirm">Пароль и срок; возвращает причину отказа либо <c>null</c>.</param>
    /// <param name="second">Имя того, кто подтвердил ПЕРВЫМ. Не пусто — окно работает вторым
    /// подтверждением (принцип двух рук, 10.3): срок не спрашивается, он уже назначен первым, а
    /// второе подтверждение — допуск к согласованному, а не новое согласование.</param>
    public DirectionConfirmDialog(string what, Func<string, int, string?> confirm,
                                  string second = "")
    {
        InitializeComponent();

        _confirm = confirm;

        WhatText.Text = what;

        DaysCombo.ItemsSource   = Terms.Select(t => t.Text).ToList();
        DaysCombo.SelectedIndex = 1;   // 90 дней — срок, на котором о направлении еще помнят

        var countersigning = second.Length > 0;

        if (countersigning)
        {
            Title            = "Второе подтверждение";
            SaveBtn.Content  = "Подтвердить вторым";
            DaysBox.IsVisible = false;
        }

        IdleHint.Text = countersigning
            ? $"Политика организации требует принципа двух рук: первым подтвердил {second}, и "
            + "второе подтверждение обязан выдать ДРУГОЙ человек. Срок остается тем, который "
            + "назначил первый: это допуск к согласованному, а не новое согласование."
            : $"Направление отключится само, если им не пользоваться "
            + $"{OutgoingDirection.IdleDays} дней: настроенный и забытый обмен — "
            + "открытая дверь, о которой никто не думает.";

        CancelBtn.Click += (_, _) => Close(false);
        SaveBtn.Click   += (_, _) => Save();
    }

    private void Save()
    {
        var password = PasswordBox.Text ?? "";

        if (password.Length == 0)
        {
            NoticeBanner.Show("Введите мастер-пароль организации.");
            return;
        }

        var days = Terms[Math.Clamp(DaysCombo.SelectedIndex, 0, Terms.Length - 1)].Days;

        if (_confirm(password, days) is { Length: > 0 } trouble)
        {
            NoticeBanner.Show(char.ToUpperInvariant(trouble[0]) + trouble[1..] + ".");
            return;
        }

        Close(true);
    }
}
