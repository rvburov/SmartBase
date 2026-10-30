using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SmartBase.Controls.Dialogs;
using SmartBase.Utils;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>Поле сообщения и выбор адресатов в настройках расширения.</summary>
public sealed class ParticipantBroadcastEditor : UserControl
{
    public const string DraftKey = "message-draft";
    public const string SendText = "Отправить сообщение";

    private readonly ExtensionSettingEditorContext _context;
    private readonly TextBox _message;
    private readonly ParticipantPicker _recipients;
    private readonly Button _send;
    private string _savedDraft;
    private bool _busy;

    /// <summary>Задача нажатия, чтобы стенд мог дождаться всех диалогов и завершения отправки.</summary>
    internal Task? PendingSend { get; private set; }

    public ParticipantBroadcastEditor(ExtensionSettingEditorContext context)
    {
        _context = context;
        _savedDraft = context.Read(DraftKey);
        _message = new TextBox
        {
            Name = "BroadcastMessage",
            Classes = { "extSetting" },
            Watermark = "Введите сообщение...",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 90,
            MaxHeight = 220,
            VerticalContentAlignment = VerticalAlignment.Top,
            Text = _savedDraft,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_message, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        _recipients = new ParticipantPicker(() => context.Read(DocRegistryExtension.SendersKey))
        {
            Name = "BroadcastRecipients",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _send = new Button
        {
            Name = "BroadcastSend",
            Classes = { "dialog", "auto" },
            Content = SendText,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(10, 0, 0, 0),
        };
        _send.Click += async (_, _) =>
        {
            if (_busy) return;
            PendingSend = SendAsync();
            await PendingSend;
        };

        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        actions.Children.Add(_recipients);
        Grid.SetColumn(_send, 1);
        actions.Children.Add(_send);
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(_message);
        panel.Children.Add(new TextBlock { Classes = { "desc" }, Text = "Кому отправить" });
        panel.Children.Add(actions);
        Content = panel;
        // Подписки ставятся после создания всех полей: отложенный TextChanged не должен
        // обратиться к незавершенному редактору, если построение контрола прервется.
        _message.TextChanged += (_, _) =>
        {
            // Первое присвоение не считается новым вводом: SaveDraft сверяет значение.
            SaveDraft();
            UpdateSendState();
        };
        _message.LostFocus += (_, _) => SaveDraft();
        _recipients.SelectionChanged += (_, _) => UpdateSendState();
        UpdateSendState();
    }

    private void SaveDraft()
    {
        var text = _message.Text ?? "";
        if (text == _savedDraft) return;
        _context.Save(DraftKey, text);
        _savedDraft = text;
    }

    private void UpdateSendState() => _send.IsEnabled = !_busy
        && !string.IsNullOrWhiteSpace(_message.Text) && _recipients.SelectedIds.Count > 0;

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _message.IsEnabled = !busy;
        _recipients.IsEnabled = !busy;
        UpdateSendState();
    }

    /// <summary>Снимок подтверждается один раз; повторного нажатия во время отправки нет.</summary>
    internal async Task SendAsync()
    {
        if (_busy) return;
        _recipients.RefreshParticipants();
        var text = _message.Text ?? "";
        var source = _context.Read(DocRegistryExtension.SendersKey);
        var selected = ParticipantBroadcast.Select(source, _recipients.SelectedIds);
        if (string.IsNullOrWhiteSpace(text) || selected.Count == 0 || _context.Owner() is not { } owner)
        {
            UpdateSendState();
            return;
        }

        SetBusy(true);
        try
        {
            SaveDraft();
            // Как у кнопки инструкции: нажатие берет и сохраняет текущий ввод, даже если
            // пользователь сразу щелкнул кнопку и LostFocus еще не успел сохранить список.
            _context.Save(DocRegistryExtension.SendersKey, source);
            var replies = _context.Replies();
            if (replies is null)
            {
                await Info(owner, "Сообщение отправить не через что: интеграции недоступны.");
                return;
            }
            var scenario = replies.Route(DocRegistryExtension.ExtensionId, new RegisterMessageAction(),
                RegisterMessageAction.SenderField, out var problem);
            if (scenario is null)
            {
                await Info(owner, $"Сообщение отправить не через что: {problem}.");
                return;
            }
            if (!await ReviewDialog(owner, "Отправить сообщение выбранным участникам?\n\n"
                    + string.Join("\n", selected.Select(p => p.Label))
                    + $"\n\nСообщение:\n{text}\n\nСообщение уйдет через сценарий «{scenario}».",
                    confirmText: "Отправить").ShowDialog<bool>(owner))
                return;

            using var cancel = new CancellationTokenSource();
            var progress = new ProgressDialog { Title = "Регистрация КД" };
            progress.SetProgress(0, "Отправка сообщения…");
            progress.EnableCancel(cancel.Cancel);
            var closed = progress.ShowDialog(owner);
            var finished = false;
            BroadcastReport report;
            try
            {
                // Backend получает обычный текст: экранирование разметки выполняется там
                // ровно один раз, путь сценария сам заменяет переносы строк.
                report = await ParticipantBroadcast.SendAsync(replies, selected, text,
                    new Progress<BroadcastProgress>(state =>
                    {
                        if (!finished) progress.SetProgress((double)state.Completed / state.Total,
                            $"Отправка: {state.Recipient.Label}");
                    }), cancel.Token);
            }
            finally
            {
                finished = true;
                progress.Done();
                await closed;
            }
            await Info(owner, ReportText(report));
        }
        catch (Exception error)
        {
            // Обработчик Click асинхронный: ошибка пути или окна должна стать сообщением,
            // а не необработанным исключением UI. Уже подтвержденные отправки не повторяем.
            await Info(owner, $"Не удалось завершить отправку сообщения: {error.Message}");
        }
        finally
        {
            // Черновик остается после успеха, частичной отправки и отмены. Выбор адресатов
            // не превращается в «всем»: появившиеся в настройке участники не добавляются сами.
            _recipients.RefreshParticipants();
            SetBusy(false);
        }
    }

    private static string ReportText(BroadcastReport report)
    {
        if (report.Problem.Length > 0) return report.Problem;
        var text = $"Отправлено: {report.Sent} из {report.Total}.";
        if (report.Failed.Count > 0)
            text += "\n\nОшибки отправки:\n" + string.Join("\n", report.Failed.Select(f => $"{f.Recipient.Label} — {f.Problem}"));
        if (report.Interrupted is { } interrupted)
            text += $"\n\nДоставка не подтверждена: {interrupted.Label}. Проверьте получение перед повторной отправкой.";
        if (report.Cancelled) text += "\n\nОтправка остановлена.";
        if (report.NotAttempted > 0) text += $"\nОтправка не начата для участников: {report.NotAttempted}.";
        return text;
    }

    private static Task Info(Window owner, string message) => owner.IsVisible
        ? ReviewDialog(owner, message, confirmText: "Понятно", cancel: false).ShowDialog(owner)
        : Task.CompletedTask;

    /// <summary>
    /// Большой список адресатов и отчет не должны вытолкнуть кнопки за экран. Ограничение
    /// касается только рассылки: прежние подтверждения приложения остаются без изменений.
    /// </summary>
    private static ConfirmDialog ReviewDialog(Window owner, string message, string confirmText,
                                               bool cancel = true)
    {
        var dialog = new ConfirmDialog(message, confirmText, "Отмена");
        if (!cancel) dialog.FindControl<Button>("CancelBtn")!.IsVisible = false;
        var label = dialog.FindControl<TextBlock>("MessageLabel")!;
        var parent = (Panel)label.Parent!;
        var index = parent.Children.IndexOf(label);
        parent.Children.Remove(label);
        var scroll = new ScrollViewer
        {
            Name = "BroadcastReviewScroll",
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = label,
        };
        parent.Children.Insert(index, scroll);

        void Fit()
        {
            // Экраны дают физические пиксели, окно — логические. Ограничение содержимого
            // дополнительно делится на зум приложения: WindowZoom увеличивает его целиком.
            var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
            var areaHeight = screen is null ? (double)owner.FindResource("DialogHeightWide")!
                : screen.WorkingArea.Height / Math.Max(screen.Scaling, 1);
            var available = owner.ClientSize.Height > 0 ? Math.Min(areaHeight, owner.ClientSize.Height) : areaHeight;
            var zoom = Math.Max(Scaling.Current, 0.1);
            var padding = (Thickness)owner.FindResource("DialogPadding")!;
            var buttonHeight = (double)owner.FindResource("ControlHeightStandard")!;
            var limit = available * 0.85;
            dialog.MaxHeight = limit;
            scroll.MaxHeight = Math.Max(0, Math.Min(available * 0.6 / zoom,
                limit / zoom - padding.Top - padding.Bottom - buttonHeight - 16));
        }
        Fit();
        dialog.Opened += (_, _) => Fit();
        return dialog;
    }
}
