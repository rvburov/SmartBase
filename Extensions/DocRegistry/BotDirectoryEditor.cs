using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>Запросы списка и карточки пользователей, загрузка имен из API.</summary>
public sealed class BotDirectoryEditor : UserControl
{
    private readonly ExtensionSettingEditorContext _context;
    private readonly ComboBox _list;
    private readonly ComboBox _profile;
    private readonly Button _load;
    private readonly TextBlock _status;
    private CancellationTokenSource? _cancel;
    internal Task? PendingLoad { get; private set; }

    public BotDirectoryEditor(ExtensionSettingEditorContext context)
    {
        _context = context;
        ComboBox Choice(string name) => new()
        {
            Name = name, HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "Выберите запрос GET",
            DisplayMemberBinding = new Binding(nameof(ExtensionReadRequest.Name)),
        };
        _list = Choice("BotUsersListRequest");
        _profile = Choice("BotUserProfileRequest");
        _load = new Button { Name = "BotUsersRefresh", Classes = { "dialog", "auto" }, Content = "Обновить пользователей" };
        _status = new TextBlock { Name = "BotUsersStatus", Classes = { "desc" }, TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Classes = { "desc" }, Text = "Запрос списка пользователей" });
        panel.Children.Add(_list);
        panel.Children.Add(new TextBlock { Classes = { "desc" }, Text = "Запрос данных отправителя" });
        panel.Children.Add(_profile);
        panel.Children.Add(_load);
        panel.Children.Add(_status);
        Content = panel;
        RefreshChoices();
        _list.DropDownOpened += (_, _) => RefreshChoices();
        _profile.DropDownOpened += (_, _) => RefreshChoices();
        _list.SelectionChanged += (_, _) => SaveChoices();
        _profile.SelectionChanged += (_, _) => SaveChoices();
        _load.Click += async (_, _) =>
        {
            if (_cancel is not null) return;
            PendingLoad = LoadAsync();
            await PendingLoad;
        };
        DetachedFromVisualTree += (_, _) => _cancel?.Cancel();
        ShowCount();
    }

    private void RefreshChoices()
    {
        var options = _context.Requests?.Invoke()?.Requests ?? [];
        var saved = BotUsers.Directory(_context.Read(BotUsers.DirectoryKey));
        // Смена ItemsSource может вызвать SelectionChanged; сохранение читает только
        // непустой выбор, а исходные значения берутся до смены обоих списков.
        _list.ItemsSource = options;
        _profile.ItemsSource = options;
        _list.SelectedItem = options.FirstOrDefault(r => r.Id == saved.ListRequestId);
        _profile.SelectedItem = options.FirstOrDefault(r => r.Id == saved.ProfileRequestId);
    }

    private void SaveChoices()
    {
        var saved = BotUsers.Directory(_context.Read(BotUsers.DirectoryKey));
        var listId = (_list.SelectedItem as ExtensionReadRequest)?.Id ?? saved.ListRequestId;
        var profileId = (_profile.SelectedItem as ExtensionReadRequest)?.Id ?? saved.ProfileRequestId;
        var updated = saved with { ListRequestId = listId, ProfileRequestId = profileId,
            Users = listId == saved.ListRequestId && profileId == saved.ProfileRequestId ? saved.Users : null };
        var value = BotUsers.Save(updated);
        if (_context.Read(BotUsers.DirectoryKey) != value) _context.Save(BotUsers.DirectoryKey, value);
        ShowCount();
    }

    private void ShowCount() => _status.Text = $"Пользователей в списке: {BotUsers.Directory(_context.Read(BotUsers.DirectoryKey)).People.Count}.";

    internal async Task LoadAsync()
    {
        SaveChoices();
        var saved = BotUsers.Directory(_context.Read(BotUsers.DirectoryKey));
        var requests = _context.Requests?.Invoke();
        if (requests is null) { _status.Text = "Интеграции недоступны."; return; }
        var list = requests.Requests.FirstOrDefault(r => r.Id == saved.ListRequestId);
        var profile = requests.Requests.FirstOrDefault(r => r.Id == saved.ProfileRequestId);
        if (list is null || profile is null || list.ConnectionId != profile.ConnectionId)
        { _status.Text = "Выберите запросы списка и карточки пользователя через одно соединение бота."; return; }
        using var cancel = new CancellationTokenSource();
        _cancel = cancel;
        _list.IsEnabled = _profile.IsEnabled = _load.IsEnabled = false;
        _status.Text = "Получение пользователей из API…";
        try
        {
            var result = await requests.ReadAsync(list.Id, new Dictionary<string, string>(), cancel.Token);
            if (!result.Ok) { _status.Text = "Не удалось обновить пользователей: " + result.Problem; return; }
            var users = BotUsers.Read(result.Json).ToArray();
            // Описание API допускает список с идентификаторами и неполными карточками.
            // Недостающие имена получаем отдельным запросом по точному ID, без догадок
            // по псевдониму и без изменения списка доступа при частичной ошибке.
            for (var i = 0; i < users.Length; i++)
            {
                if (users[i].Complete) continue;
                _status.Text = $"Получение карточки пользователя: {i + 1} из {users.Length}…";
                var card = await requests.ReadAsync(profile.Id,
                    new Dictionary<string, string> { ["sender"] = users[i].Id }, cancel.Token);
                if (!card.Ok) throw new InvalidOperationException(card.Problem);
                users[i] = BotUsers.Read(card.Json).SingleOrDefault(u => u.Id == users[i].Id)
                    ?? throw new InvalidOperationException("API не вернул запрошенную карточку пользователя.");
            }
            cancel.Token.ThrowIfCancellationRequested();
            _context.Save(BotUsers.DirectoryKey, BotUsers.Save(saved with { Users = users }));
            ShowCount();
            var incomplete = users.Count(u => !u.Complete);
            if (incomplete > 0) _status.Text += $" Неполное ФИО в API: {incomplete}. Эти пользователи не доступны для нового выбора; сначала заполните профиль.";
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception error) { _status.Text = "Не удалось обновить пользователей: " + error.Message; }
        finally
        {
            _cancel = null;
            _list.IsEnabled = _profile.IsEnabled = _load.IsEnabled = true;
        }
    }
}
