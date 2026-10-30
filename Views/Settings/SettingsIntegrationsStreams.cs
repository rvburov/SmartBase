using Avalonia.Controls;
using SmartBase.Controls.Dialogs;
using SmartBase.Database;
using SmartBase.Integrations;
using static SmartBase.Controls.Widgets.SettingRows;

namespace SmartBase.Views.Settings;

/// <summary>
/// Вкладка «Потоки» раздела «Интеграции» (partial SettingsView): что нам присылают.
///
/// Список карточек, как у соединений и запросов, — с одной добавкой, которой нет у них: строка
/// СОСТОЯНИЯ, посчитанная вживую. Запрос живет мгновение, и о нем говорит журнал; поток открыт
/// часами, и человеку нужен ответ «слушается ли он прямо сейчас» — здесь, а не в журнале, где
/// последняя строка о нем могла лечь вчера.
///
/// Карточка правится в своем окне (<see cref="IntegrationStreamDialog"/>), хранит все
/// <c>IntegrationsRegistry</c>, слушает потоки <see cref="StreamService"/>.
/// </summary>
public partial class SettingsView
{
    /// <summary>
    /// Состояние потока изменилось: подключился, потерял связь. Зовет MainWindow из службы
    /// потоков; экран перестраивается, только если вкладка открыта, — иначе это работа впустую.
    /// </summary>
    public void OnStreamChanged()
    {
        if (_integrationsTab == "streams") BuildIntegrationsStreams();
        if (_integrationsTab == "journal") BuildIntegrationsJournal();
    }

    private void BuildIntegrationsStreams()
    {
        var rows = IntegrationsStreamsTab.Children;
        rows.Clear();

        if (Integrations is null)
        {
            rows.Add(Row("Потоки", "Список станет доступен, когда откроется база данных."));
            return;
        }

        var registry    = Integrations;
        var connections = registry.Connections();

        // Поток без соединения открыть негде, и заводить его бессмысленно.
        if (connections.Count == 0)
        {
            rows.Add(Row("Сначала нужно соединение",
                "Поток открывается через соединение: адрес и секрет берутся оттуда. "
                + "Заведите соединение на вкладке «Соединения»."));
            return;
        }

        rows.Add(ActionsRow("Добавить", () => _ = EditStreamAsync(IntegrationStream.New())));

        var streams = registry.Streams();

        if (streams.Count == 0)
        {
            rows.Add(Row("Потоков пока нет",
                "Поток — это «что нам присылают»: соединение, по которому сторонняя программа "
                + "сама сообщает о случившемся. Программа открывает его и слушает, а каждое "
                + "событие заводит сценарий, выбравший этот поток своим запуском."));
            return;
        }

        foreach (var stream in streams)
        {
            rows.Add(StreamRow(registry, stream, connections));
            rows.Add(Separator());
        }
    }

    private Border StreamRow(IntegrationsRegistry registry, IntegrationStream stream,
                             IReadOnlyList<IntegrationConnection> connections)
    {
        var connection = connections.FirstOrDefault(c => c.Id == stream.ConnectionId);

        var description = $"{connection?.Name ?? "соединение не найдено"} · {stream.Describe()}";

        var facts = new List<string> { StateText(registry, stream) };

        if (stream.ItemsPath.Length > 0) facts.Add("записи в событии: " + stream.ItemsPath);
        if (stream.KeyField.Length  > 0) facts.Add("ключ события: " + stream.KeyField);

        return CardRow(stream.Name, description, string.Join(" · ", facts),
            edit:   () => _ = EditStreamAsync(stream),
            delete: () => _ = DeleteStreamAsync(stream));
    }

    /// <summary>
    /// Слушается ли поток прямо сейчас — словами. Ответов четыре, и путать их нельзя: «не
    /// слушается» лечится включением сценария, «нет связи» — проверкой соединения со сторонней программой.
    /// </summary>
    private string StateText(IntegrationsRegistry registry, IntegrationStream stream)
    {
        if (_streams?.State(stream.Id) is { } state)
        {
            var since = state.Since.ToLocalTime().ToString("dd.MM HH:mm");

            if (state.Connected)          return $"Слушается с {since}";
            if (state.Trouble.Length > 0) return $"С {since} {state.Trouble}";

            return "Подключается…";
        }

        var scenarios = registry.ScenariosOfStream(stream.Id);

        if (scenarios.Count == 0)
            return "Не слушается: нет сценария, который заводят события этого потока";

        return scenarios.Any(s => s.Enabled)
            ? "Не слушается: служба потоков еще не запущена"
            : "Не слушается: сценарии этого потока выключены";
    }

    // ── Правка ────────────────────────────────────────────────────────────────

    private async Task EditStreamAsync(IntegrationStream card)
    {
        if (VisualRoot is not Window win || Integrations is null) return;

        var registry = Integrations;

        var result = await new IntegrationStreamDialog(card, registry.Connections(), registry.Secret)
            .ShowDialog<IntegrationStream?>(win);

        if (result is null) return;

        try
        {
            registry.SaveStream(result);
        }
        catch (InvalidOperationException error)
        {
            await ConfirmDialog.Info(error.Message).ShowDialog(win);
            return;
        }

        // Правка карточки перезапускает слушание сразу: ждать полминуты до плановой сверки
        // значило бы полминуты слушать поток по старому адресу.
        _streams?.Sync();

        BuildIntegrationsStreams();
    }

    private async Task DeleteStreamAsync(IntegrationStream stream)
    {
        if (VisualRoot is not Window win || Integrations is null) return;

        var registry = Integrations;
        var used     = registry.ScenariosOfStream(stream.Id);

        // Поток, события которого заводят сценарии, не удаляется: оставить сценарий без того,
        // что его заводит, хуже, чем отказать.
        if (used.Count > 0)
        {
            await ConfirmDialog.Info(
                    $"События потока «{stream.Name}» заводят сценарии ({used.Count}): "
                    + string.Join(", ", used.Select(s => s.Name))
                    + ".\n\nПереключите их на другой запуск или удалите.")
                .ShowDialog(win);
            return;
        }

        var confirmed = await new ConfirmDialog($"Удалить поток «{stream.Name}»?")
            .ShowDialog<bool>(win);

        if (confirmed != true) return;

        registry.DeleteStream(stream.Id);
        _streams?.Sync();

        BuildIntegrationsStreams();
    }
}
