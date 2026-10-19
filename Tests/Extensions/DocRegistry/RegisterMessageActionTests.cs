using SmartBase.Database;
using SmartBase.Core.Models;
using SmartBase.Extensions;
using SmartBase.Extensions.DocRegistry;
using SmartBase.Integrations;
using SmartBase.Utils;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>
/// Регистрация по сообщению (<see cref="RegisterMessageAction"/>): допуск, выдача номера по
/// маске и два вида отказа.
///
/// Разбор самого сообщения проверяет <see cref="MessageRegistrationTests"/>, выдачу номеров —
/// <see cref="DocRegistrarTests"/>. Здесь — то, что видно только вместе: кому действие отвечает
/// словами отправителя, а где молчит и оставляет ответ сценарию.
/// </summary>
public sealed class RegisterMessageActionTests
{
    private static readonly DateTimeOffset Now =
        new(new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Local));

    private static readonly RegisterMessageAction Action = new();

    private static IReadOnlyDictionary<string, string> Settings(string senders = "1001 Иванов; 1002") =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DocRegistryExtension.OrgCodesKey] = "АБВГ, ГМБВ",
            [DocRegistryExtension.SendersKey]  = senders,
        };

    private static ActionInput Input(string text, string sender = "1001") =>
        new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RegisterMessageAction.TextField]   = text,
            [RegisterMessageAction.SenderField] = sender,
        });

    private static string Message(string number = "АБВГ.301121.ХХХ", string project = "Машина") =>
        $"Действие: Регистрация\nНомер: {number}\nНаименование: Кронштейн\n"
        + $"Проект: {project}\nФИО: Иванов Иван Иванович";

    private static ActionContext Context(TempDatabase db, int collectionId, string senders = "1001 Иванов; 1002") =>
        new(db.Constructor, 1, collectionId, Now, Settings(senders));

    [Theory]
    [InlineData("Инструкция")]
    [InlineData("  ИНСТРУКЦИЯ  ")]
    [InlineData("<p>Инструкция</p>")]
    public void Instructions_need_no_database_and_explain_all_operations(string command)
    {
        var result = Action.Run(new ActionContext(null, 1, 1, Now, Settings()), Input(command));
        Assert.True(result.Ok, result.Explanation);
        Assert.Equal(0, result.Rows);
        foreach (var action in new[] { "Регистрация", "Просмотр", "Удаление", "Изменение" })
            Assert.Contains($"Действие: {action}\nНомер:", result.Answer, StringComparison.Ordinal);
        Assert.Contains("не найден", result.Answer, StringComparison.Ordinal);
        Assert.Contains("10 минут", result.Answer, StringComparison.Ordinal);
        Assert.DoesNotContain("```", result.Answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Instructions_require_an_allowed_sender()
    {
        var result = Action.Run(new ActionContext(null, 1, 1, Now, Settings()),
                                Input("Инструкция", "unknown"));
        Assert.False(result.Ok);
        Assert.Equal(RegisterMessageAction.NotListed, result.Answer);
    }

    // ── Регистрация ───────────────────────────────────────────────────────────

    /// <summary>
    /// Сообщение становится строкой журнала, а ответ — что зарегистрировано: теми же строками,
    /// что в сообщении, с выданным номером на месте метки. В журнал запусков идет одна строка.
    /// </summary>
    [Fact]
    public void A_message_becomes_a_registered_document()
    {
        using var db = new TempDatabase();
        var journal = JournalStand.Create(db.Constructor);

        var result = Action.Run(Context(db, journal), Input(Message()));

        Assert.Empty(db.Constructor.GetRecords(journal));
        Assert.Contains("первый номер", result.Answer, StringComparison.Ordinal);
        result = Action.Run(Context(db, journal), Input("Да"));

        Assert.True(result.Ok, result.Explanation);
        Assert.Equal(
            "Зарегистрировано\n"
            + "Номер: АБВГ.301121.001\n"
            + "Наименование: Кронштейн\n"
            + "Проект: Машина\n"
            + "ФИО: Иванов Иван Иванович",
            result.Answer);
        Assert.Equal("Зарегистрировано АБВГ.301121.001 Машина Иванов Иван Иванович", result.Explanation);
        Assert.Equal(1, result.Rows);

        var record = Assert.Single(db.Constructor.GetRecords(journal));
        Assert.Equal("Кронштейн", record.Title);
    }

    /// <summary>Метка номера — следующий свободный в серии, и счет ведет журнал.</summary>
    [Fact]
    public void The_number_mask_takes_the_next_free_number()
    {
        using var db = new TempDatabase();
        var journal = JournalStand.Create(db.Constructor);
        var context = Context(db, journal);

        Action.Run(context, Input(Message()));
        Action.Run(context, Input("Да"));
        var second = Action.Run(context, Input(Message()));

        Assert.True(second.Ok, second.Explanation);
        Assert.Contains("АБВГ.301121.002", second.Answer, StringComparison.Ordinal);
    }

    /// <summary>
    /// Метка исполнения — следующее свободное исполнение НАЗВАННОГО документа. Номер при этом
    /// остается тем же: исполнение, уехавшее к следующему номеру серии, означало бы совсем
    /// другой документ, а документ 001 получил бы дубль.
    /// </summary>
    [Fact]
    public void The_execution_mask_takes_the_next_free_execution()
    {
        using var db = new TempDatabase();
        var journal = JournalStand.Create(db.Constructor);
        var context = Context(db, journal);

        Action.Run(context, Input(Message()));
        Action.Run(context, Input("Да"));   // АБВГ.301121.001

        var first  = Action.Run(context, Input(Message("АБВГ.301121.001-ХХ")));
        var second = Action.Run(context, Input(Message("АБВГ.301121.001-ХХ")));

        Assert.Contains("Номер: АБВГ.301121.001-01\n", first.Answer,  StringComparison.Ordinal);
        Assert.Contains("Номер: АБВГ.301121.001-02\n", second.Answer, StringComparison.Ordinal);

        // Три строки, и номер 001 без исполнения среди них один.
        var titles = db.Constructor.GetRecords(journal);
        Assert.Equal(3, titles.Count);
    }

    /// <summary>
    /// Исполнение документа, которого в журнале нет, заводится: старые документы в журнал часто
    /// не заносились — правило то же, что у загрузки списком из файла.
    /// </summary>
    [Fact]
    public void An_execution_of_an_unlisted_document_is_registered()
    {
        using var db = new TempDatabase();
        var journal = JournalStand.Create(db.Constructor);

        var result = Action.Run(Context(db, journal), Input(Message("АБВГ.301121.007-ХХ")));

        Assert.True(result.Ok, result.Explanation);
        Assert.Contains("АБВГ.301121.007-01", result.Answer, StringComparison.Ordinal);
    }

    // ── Допуск ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Того, кого нет в списке, действие не регистрирует — и сообщение его не разбирает: чужому
    /// перечень ошибок его сообщения ни к чему.
    /// </summary>
    [Fact]
    public void A_sender_outside_the_list_is_refused()
    {
        using var db = new TempDatabase();
        var journal = JournalStand.Create(db.Constructor);

        var result = Action.Run(Context(db, journal), Input("не сообщение вовсе", sender: "9999"));

        Assert.False(result.Ok);
        Assert.Equal(RegisterMessageAction.NotListed, result.Answer);
        Assert.Contains("9999", result.Explanation, StringComparison.Ordinal);
        Assert.Empty(db.Constructor.GetRecords(journal));
    }

    /// <summary>Сверяется идентификатор, а не пометка рядом с ним: «Иванов» — не отправитель.</summary>
    [Fact]
    public void The_note_next_to_an_identifier_is_not_a_sender()
    {
        using var db = new TempDatabase();
        var journal = JournalStand.Create(db.Constructor);

        var result = Action.Run(Context(db, journal), Input(Message(), sender: "Иванов"));

        Assert.False(result.Ok);
        Assert.Equal(RegisterMessageAction.NotListed, result.Answer);
    }

    /// <summary>
    /// Пустой список — «никому», а не «всем». Отправителю — что его нет в списке; тому, кто
    /// настраивал, — что список пуст: иначе он искал бы отправителя в списке, которого нет.
    /// </summary>
    [Fact]
    public void An_empty_list_lets_nobody_in()
    {
        using var db = new TempDatabase();
        var journal = JournalStand.Create(db.Constructor);

        var result = Action.Run(Context(db, journal, senders: ""), Input(Message()));

        Assert.False(result.Ok);
        Assert.Equal(RegisterMessageAction.NotListed, result.Answer);
        Assert.Contains("пуст", result.Explanation, StringComparison.Ordinal);
        Assert.Empty(db.Constructor.GetRecords(journal));
    }

    /// <summary>
    /// Отправитель не назван — это ошибка настройки шага, а не сообщения: отправителю сказать
    /// нечего, и что ему ответить, решает сценарий.
    /// </summary>
    [Fact]
    public void A_missing_sender_is_a_failure_without_words_for_the_sender()
    {
        using var db = new TempDatabase();
        var journal = JournalStand.Create(db.Constructor);

        var result = Action.Run(Context(db, journal), Input(Message(), sender: ""));

        Assert.False(result.Ok);
        Assert.Equal("", result.Answer);
        Assert.Contains("Отправитель", result.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Поле «Текст сообщения» не заполнено в шаге — ошибка настройки: отправитель написал все
    /// верно, и перечень «не указано…» отправил бы его искать ошибку, которой у него нет.
    /// </summary>
    [Fact]
    public void A_step_without_the_text_field_is_a_setup_failure()
    {
        using var db = new TempDatabase();
        var journal = JournalStand.Create(db.Constructor);

        var input = new ActionInput(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RegisterMessageAction.SenderField] = "1001",
        });

        var result = Action.Run(Context(db, journal), input);

        Assert.False(result.Ok);
        Assert.Equal("", result.Answer);
        Assert.Contains("Текст сообщения", result.Explanation, StringComparison.Ordinal);
        Assert.Empty(db.Constructor.GetRecords(journal));
    }

    /// <summary>
    /// Колонка с текстом пришла пустой: отправителю — перечень того, чего не хватает, а в журнале
    /// рядом сказано, что пустым был сам текст.
    /// </summary>
    [Fact]
    public void An_empty_text_is_answered_and_noted_in_the_journal()
    {
        using var db = new TempDatabase();
        var journal = JournalStand.Create(db.Constructor);

        var result = Action.Run(Context(db, journal), Input(""));

        Assert.False(result.Ok);
        Assert.StartsWith("Действие: не указано действие", result.Answer, StringComparison.Ordinal);
        Assert.DoesNotContain("пустым", result.Answer, StringComparison.Ordinal);
        Assert.Contains("пришел пустым", result.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// В поле шага названа не колонка, а постоянный текст: действие получает его вместо
    /// сообщения. Журнал называет длину полученного — по ней это и видно; сам текст в журнал не
    /// идет.
    /// </summary>
    [Fact]
    public void A_text_without_message_lines_is_noted_by_its_length()
    {
        using var db = new TempDatabase();
        var journal = JournalStand.Create(db.Constructor);

        var result = Action.Run(Context(db, journal), Input("{text}"));

        Assert.False(result.Ok);
        Assert.StartsWith("Действие: не указано действие", result.Answer, StringComparison.Ordinal);
        Assert.Contains("знаков: 6", result.Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("{text}", result.Explanation, StringComparison.Ordinal);
    }

    /// <summary>Узнана хоть одна строка — это сообщение с ошибками, и пометки в журнале нет.</summary>
    [Fact]
    public void A_message_with_problems_carries_no_note()
    {
        using var db = new TempDatabase();
        var journal = JournalStand.Create(db.Constructor);

        var result = Action.Run(Context(db, journal), Input("Проект: Машина"));

        Assert.False(result.Ok);
        Assert.Equal(result.Answer, result.Explanation);
    }

    // ── Два вида отказа ───────────────────────────────────────────────────────

    /// <summary>Ошибки сообщения — отправителю, все сразу и по строке на поле.</summary>
    [Fact]
    public void Message_problems_are_addressed_to_the_sender()
    {
        using var db = new TempDatabase();
        var journal = JournalStand.Create(db.Constructor);

        var result = Action.Run(Context(db, journal), Input(Message("АБВГ.301121.000", project: "")));

        Assert.False(result.Ok);
        Assert.Equal(
            "Номер: неправильный номер (Пример: АААА.000000.ХХХ, АААА.000000.001 или АААА.000000.001-ХХ)\n"
            + "Проект: не указан проект",
            result.Answer);
        Assert.Empty(db.Constructor.GetRecords(journal));
    }

    /// <summary>
    /// Коллекция не подходит под журнал — причина в настройке, и отправитель ее не исправит:
    /// слов для него нет, а объяснение остается тому, кто настраивал.
    /// </summary>
    [Fact]
    public void A_wrong_collection_is_a_failure_without_words_for_the_sender()
    {
        using var db = new TempDatabase();
        var wrong = JournalStand.Create(db.Constructor, "Не журнал", ["Обозначение", "Что-то еще"]);

        var result = Action.Run(Context(db, wrong), Input(Message()));

        Assert.False(result.Ok);
        Assert.Equal("", result.Answer);
        Assert.Contains("не подходит под журнал", result.Explanation, StringComparison.Ordinal);
    }

    // ── Расширение ────────────────────────────────────────────────────────────

    /// <summary>Действие объявлено расширением — иначе в списке шагов его не будет.</summary>
    [Fact]
    public void The_extension_offers_the_action()
    {
        var action = Assert.Single(((IExtension)new DocRegistryExtension()).Actions,
                                   a => a.Key == RegisterMessageAction.ActionKey);

        Assert.Equal(ExtensionActionKind.Operation, action.Kind);
        Assert.Equal([RegisterMessageAction.TextField, RegisterMessageAction.SenderField],
                     action.Fields.Select(f => f.Key));

        // Отправитель обязателен, текст — нет: на пустое сообщение отвечают перечнем ошибок.
        Assert.False(action.Fields[0].Required);
        Assert.True(action.Fields[1].Required);
    }
}

/// <summary>
/// Регистрация по сообщению В РАБОТАЮЩЕМ сценарии: строка события — шаг — ответ отправителю.
/// Оболочка при этом не знает о регистрации ничего, а расширение — о том, откуда пришла строка.
/// </summary>
public sealed class MessageScenarioTests : IDisposable
{
    private readonly TempDatabase     _db  = new("message");
    private readonly TempDir          _dir = new("message");
    private readonly DatabaseRegistry _registry;

    public MessageScenarioTests()
    {
        _registry = DatabaseRegistry.Open(Path.Combine(_dir.Path, "database.db"),
                                          Path.Combine(_dir.Path, "app.db"));

        // Настройки расширения читает оболочка — из того же хранилища, что и в программе.
        AppSettings.AttachStore(_registry);
        AppSettings.SaveExtensionSetting(DocRegistryExtension.ExtensionId,
                                         DocRegistryExtension.OrgCodesKey, "АБВГ");
        AppSettings.SaveExtensionSetting(DocRegistryExtension.ExtensionId,
                                         DocRegistryExtension.SendersKey, "1001 Иванов");
    }

    public void Dispose()
    {
        // Хранилище настроек общее на процесс: оставить в нем закрытый реестр значило бы уронить
        // следующий тест, который о настройках и не думал.
        AppSettings.AttachStore(new NoSettings());

        RunJournal.Attach(null);
        _registry.Dispose();
        _db.Dispose();
        _dir.Dispose();
    }

    private sealed class NoSettings : ISettingsDatabase
    {
        public string? Get(string key) => null;
        public void Set(string key, string value) { }
        public void Remove(string key) { }
        public IReadOnlyList<string> KeysWithPrefix(string prefix) => [];
    }

    /// <summary>Сценарий потока: поток и его соединение заведены, шаг — регистрация по сообщению.</summary>
    private Scenario Scenario(int journal)
    {
        var connection = _registry.Integrations.Save(IntegrationConnection.New() with
        {
            Name = "Сторонняя программа", Address = "http://127.0.0.1:1",
        });

        var stream = _registry.Integrations.SaveStream(IntegrationStream.New() with
        {
            Name = "События", ConnectionId = connection.Id, Path = "/events", ItemsPath = "data",
        });

        return SmartBase.Integrations.Scenario.New() with
        {
            Name    = "Регистрация по сообщению",
            Enabled = true,
            Trigger = ScenarioTrigger.OnStream(stream.Id),
            Steps   =
            [
                ScenarioStep.OfFilter(new ScenarioCondition("kind", ScenarioTest.Equals, "message")),
                ScenarioStep.OfAction(DocRegistryExtension.ExtensionId, RegisterMessageAction.ActionKey,
                                      _db.Manager.DbId, journal,
                                      new Dictionary<string, string>
                                      {
                                          [RegisterMessageAction.TextField]   = "{{body}}",
                                          [RegisterMessageAction.SenderField] = "{{author}}",
                                      }),
            ],
        };
    }

    private Task<ScenarioOutcome> Run(Scenario scenario, string json)
    {
        var row = Assert.Single(StreamEvents.Rows(json, "data", out var trouble));
        Assert.Equal("", trouble);

        return ScenarioRunner.RunAsync(scenario, _registry.Integrations, [_db.Manager],
                                       [new DocRegistryExtension()], DateTimeOffset.Now, _ => { },
                                       default, new StepTable([.. row.Keys], [row]));
    }

    private static string Event(string body, string author = "1001", string kind = "message") =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            kind,
            data = new[] { new { body, author } },
        });

    private const string Message =
        "Действие: Регистрация\nНомер: АБВГ.301121.ХХХ\nНаименование: Кронштейн\n"
        + "Проект: Машина\nФИО: Иванов Иван Иванович";

    private const string Registered =
        "Зарегистрировано\nНомер: АБВГ.301121.001\nНаименование: Кронштейн\n"
        + "Проект: Машина\nФИО: Иванов Иван Иванович";

    /// <summary>
    /// Текст события пришел разметкой — так программы переписки присылают сообщение в несколько
    /// строк. Документ регистрируется, как если бы текст пришел строками.
    /// </summary>
    [Fact]
    public async Task A_message_in_markup_is_registered_too()
    {
        var journal = JournalStand.Create(_db.Constructor);

        var scenario = Scenario(journal);
        var outcome = await Run(scenario,
                                Event("<span>" + Message.Replace("\n", "<br>") + "</span>"));

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Empty(_db.Constructor.GetRecords(journal));
        outcome = await Run(scenario, Event("Да"));
        Assert.Equal(Registered, outcome.Reply);
        Assert.Single(_db.Constructor.GetRecords(journal));
    }

    [Fact]
    public async Task An_event_row_becomes_a_registered_document_and_a_reply()
    {
        var journal = JournalStand.Create(_db.Constructor);

        var scenario = Scenario(journal);
        var outcome = await Run(scenario, Event(Message));

        Assert.True(outcome.Ok, outcome.Problem);
        Assert.Empty(_db.Constructor.GetRecords(journal));
        outcome = await Run(scenario, Event("Да"));
        Assert.Equal(Registered, outcome.Reply);
        Assert.Single(_db.Constructor.GetRecords(journal));

        // В отчете шагов — одна строка: отчет читает тот, кто настраивал обмен.
        Assert.Contains(outcome.Lines, line => line.Contains(
            "Зарегистрировано АБВГ.301121.001 Машина Иванов Иван Иванович", StringComparison.Ordinal));
    }

    /// <summary>Каждое сообщение проходит новый сценарий; ожидание ответа между ними живет.</summary>
    [Theory]
    [InlineData("Удаление")]
    [InlineData("Изменение")]
    public async Task A_followup_message_confirms_the_operation_and_returns_its_reply(string action)
    {
        var journal = JournalStand.Create(_db.Constructor);
        var scenario = Scenario(journal);
        Assert.True((await Run(scenario, Event(Message))).Ok);
        Assert.True((await Run(scenario, Event("Да"))).Ok);
        var request = $"Действие: {action}\nНомер: АБВГ.301121.001"
                    + (action == "Изменение" ? "\nПроект: Станок" : "");
        var preview = await Run(scenario, Event(request));
        Assert.True(preview.Ok, preview.Problem);
        Assert.Contains("Подтвердите", preview.Reply, StringComparison.Ordinal);
        Assert.Single(_db.Constructor.GetRecords(journal));

        var otherSource = await Run(Scenario(journal), Event("Да"));
        Assert.False(otherSource.Ok);
        Assert.Single(_db.Constructor.GetRecords(journal));
        var confirmed = await Run(scenario, Event("Да"));
        Assert.True(confirmed.Ok, confirmed.Problem);
        Assert.StartsWith(action == "Удаление" ? "Удалено\n" : "Изменено\n", confirmed.Reply, StringComparison.Ordinal);
        if (action == "Удаление") Assert.Empty(_db.Constructor.GetRecords(journal));
        else Assert.Contains("Проект: Станок", confirmed.Reply, StringComparison.Ordinal);
        Assert.False((await Run(scenario, Event("Да"))).Ok);
    }

    [Fact]
    public async Task Instructions_leave_a_pending_confirmation_unchanged()
    {
        var journal = JournalStand.Create(_db.Constructor);
        var scenario = Scenario(journal);
        Assert.True((await Run(scenario, Event(Message))).Ok);
        Assert.True((await Run(scenario, Event("Да"))).Ok);
        Assert.True((await Run(scenario, Event("Действие: Удаление\nНомер: АБВГ.301121.001"))).Ok);
        var help = await Run(scenario, Event("Инструкция"));
        Assert.True(help.Ok, help.Problem);
        Assert.StartsWith("Инструкция по работе с журналом КД", help.Reply, StringComparison.Ordinal);
        Assert.Single(_db.Constructor.GetRecords(journal));
        Assert.True((await Run(scenario, Event("Да"))).Ok);
        Assert.Empty(_db.Constructor.GetRecords(journal));
    }

    [Fact]
    public async Task A_readonly_connection_can_view_but_cannot_confirm_deletion()
    {
        var journal = JournalStand.Create(_db.Constructor);
        var scenario = Scenario(journal);
        Assert.True((await Run(scenario, Event(Message))).Ok);
        Assert.True((await Run(scenario, Event("Да"))).Ok);
        Assert.True((await Run(scenario, Event("Действие: Удаление\nНомер: АБВГ.301121.001"))).Ok);
        var userId = _db.Permissions.CurrentUserId;
        _db.Permissions.SetMask(userId, null, (int)PermAction.ViewCollection);
        var me = _db.Context.DbUsers.Single(u => u.Id == userId);
        me.IsAdmin = false;
        _db.Context.SaveChanges();
        _db.Permissions.Reload(notify: false);

        var viewed = await Run(scenario, Event("Действие: Просмотр\nНомер: АБВГ.301121.001"));
        Assert.True(viewed.Ok, viewed.Problem);
        Assert.Contains("Наименование: Кронштейн", viewed.Reply, StringComparison.Ordinal);
        var denied = await Run(scenario, Event("Да"));
        Assert.False(denied.Ok);
        Assert.Contains("Недостаточно прав", denied.Reply, StringComparison.Ordinal);
        Assert.Single(_db.Constructor.GetRecords(journal));
    }

    /// <summary>Ошибки сообщения доезжают до ответа словами действия, без приставок оболочки.</summary>
    [Fact]
    public async Task Message_problems_reach_the_reply_as_the_action_said_them()
    {
        var journal = JournalStand.Create(_db.Constructor);

        var outcome = await Run(Scenario(journal), Event("Действие: Регистрация"));

        Assert.False(outcome.Ok);
        Assert.StartsWith("Номер: не указан номер", outcome.Reply, StringComparison.Ordinal);
        Assert.DoesNotContain("действие «", outcome.Reply, StringComparison.Ordinal);
        Assert.Empty(_db.Constructor.GetRecords(journal));
    }

    /// <summary>
    /// Событие другого вида отбор не проходит — и следа не оставляет: ни записи, ни ответа, ни
    /// строки журнала. По одному потоку ходят события разного вида, и сценарию нужны не все.
    /// </summary>
    [Fact]
    public async Task An_event_of_another_kind_leaves_no_trace()
    {
        var journal = JournalStand.Create(_db.Constructor);

        var outcome = await Run(Scenario(journal), Event(Message, kind: "status"));

        Assert.True(outcome.Idle);
        Assert.Equal("", outcome.Reply);
        Assert.Empty(_db.Constructor.GetRecords(journal));
    }

    /// <summary>
    /// Журнал не тот — причина не в сообщении: слов для отправителя нет, и что ему ответить,
    /// решает текст «при сбое» сценария.
    /// </summary>
    [Fact]
    public async Task A_setup_failure_leaves_the_reply_to_the_scenario()
    {
        var wrong = JournalStand.Create(_db.Constructor, "Не журнал", ["Обозначение"]);

        var outcome = await Run(Scenario(wrong), Event(Message));

        Assert.False(outcome.Ok);
        Assert.Equal("", outcome.Reply);
        Assert.Equal("Сервер не работает", StreamService.Answer(outcome, "Сервер не работает"));
    }
}
