using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Extensions;
using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>Вложение проходит правила сообщений и записывается одной операцией вместе с квитанцией.</summary>
public sealed class FileMessageRegistrationTests : IDisposable
{
    private readonly TempDatabase _db = new("file-message");
    private readonly object _session = new();
    private readonly RegistryShape _shape;
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private const string Mask = "АБВГ.301121.ХХХ";
    private const string Unknown = "АБВГ.806121.ХХХ";

    public FileMessageRegistrationTests() =>
        _shape = JournalStand.Shape(_db.Constructor, JournalStand.Create(_db.Constructor));

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Existing_series_write_every_rows_own_fields_without_requiring_message_text()
    {
        Seed();
        Seed(code: "301122");
        var file = FileMessageBook.Create(
            new FileMessageBook.Row(2, Mask, "Втулка", Accounting: "Учтен", FirstUse: "Изделие 1", Summary: "Сталь"),
            new FileMessageBook.Row(6, "АБВГ.301122.ХХХ", "Корпус", "Петров Петр Петрович", "Другой проект"));

        var result = File(file);

        Assert.True(result.Ok, result.Explanation);
        Assert.Equal(2, result.Rows);
        Assert.Contains("2 из 2", ReplyMarkup.Text(result.Answer), StringComparison.Ordinal);
        Assert.DoesNotContain("Подтвердите", ReplyMarkup.Text(result.Answer), StringComparison.Ordinal);
        var first = Values("Втулка");
        Assert.Equal("АБВГ.301121.002", first[_shape.DesignationFieldId]);
        Assert.Equal("Иванов Иван Иванович", first[_shape.FioFieldId]);
        Assert.Equal("Проект", first[_shape.ProjectFieldId]);
        Assert.Equal("Учтен", first[_shape.AccountingFieldId]);
        Assert.Equal("Изделие 1", first[_shape.FirstUseFieldId]);
        Assert.Equal("Сталь", first[_shape.SummaryFieldId]);
        var second = Values("Корпус");
        Assert.Equal("АБВГ.301122.002", second[_shape.DesignationFieldId]);
        Assert.Equal("Петров Петр Петрович", second[_shape.FioFieldId]);
        Assert.Equal("Другой проект", second[_shape.ProjectFieldId]);
        Assert.Equal(first[_shape.DateFieldId], second[_shape.DateFieldId]);
        Assert.True(DateValue.TryParseStorage(first[_shape.DateFieldId], out var date));
        Assert.Equal(Now.LocalDateTime, date.Start);
    }

    [Fact]
    public void Repeated_masks_fill_holes_and_executions_in_sheet_order_using_bot_normalization()
    {
        Seed();
        Seed(number: 3);
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2, "AБBГ.301121.XXX", "Первый"),
            new FileMessageBook.Row(3, Mask, "Второй"), new FileMessageBook.Row(4, "АБВГ.301121.014-ХХ", "Исполнение 1"),
            new FileMessageBook.Row(5, "АБВГ.301121.014-ХХ", "Исполнение 2"));

        var result = File(bytes);

        Assert.True(result.Ok, result.Explanation);
        Assert.Equal(4, result.Rows);
        Assert.Equal("АБВГ.301121.002", Values("Первый")[_shape.DesignationFieldId]);
        Assert.Equal("АБВГ.301121.004", Values("Второй")[_shape.DesignationFieldId]);
        Assert.Equal("АБВГ.301121.014-01", Values("Исполнение 1")[_shape.DesignationFieldId]);
        Assert.Equal("АБВГ.301121.014-02", Values("Исполнение 2")[_shape.DesignationFieldId]);
    }

    [Fact]
    public void The_file_obeys_the_bots_existing_exact_free_number_rule()
    {
        Seed();
        var result = File(FileMessageBook.Create(new FileMessageBook.Row(2, "АБВГ.301121.014")));

        Assert.True(result.Ok, result.Explanation);
        Assert.Equal(1, result.Rows);
        Assert.Equal("АБВГ.301121.014", Values("Документ")[_shape.DesignationFieldId]);
    }

    [Fact]
    public void All_errors_keep_real_sheet_lines_and_block_every_valid_row()
    {
        Seed();
        var before = Snapshot(_db.Context);
        var file = FileMessageBook.Create(new FileMessageBook.Row(2, Mask, "Годная строка"),
            new FileMessageBook.Row(5, "АБВГ.30112.ХХХ", "Ошибка кода"),
            new FileMessageBook.Row(9, "АБВГ.301121.ХХХ-ХХ", "Ошибка маски"),
            new FileMessageBook.Row(12, Mask, "", "И. И. Иванов", ""));

        var result = File(file);

        Assert.False(result.Ok);
        var reply = ReplyMarkup.Text(result.Answer);
        Assert.Contains("0 из 4", reply, StringComparison.Ordinal);
        foreach (var line in new[] { 5, 9, 12 }) Assert.Contains($"Строка {line}", reply, StringComparison.Ordinal);
        Assert.Contains("АБВГ.30112.ХХХ", reply, StringComparison.Ordinal);
        Assert.Contains("АБВГ.301121.ХХХ-ХХ", reply, StringComparison.Ordinal);
        Assert.Contains("Наименование", reply, StringComparison.Ordinal);
        Assert.Contains("Проект", reply, StringComparison.Ordinal);
        Assert.Contains("ФИО", reply, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(_db.Context));
        Assert.False(Text("Да").Ok);
    }

    [Fact]
    public void New_catalog_and_unknown_series_wait_for_one_confirmation_for_the_entire_file()
    {
        Seed();
        Assert.NotNull(EskdCatalog.Find("301122"));
        Assert.Null(EskdCatalog.Find("806121"));
        var before = Snapshot(_db.Context);
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2, Mask, "Старая серия"),
            new FileMessageBook.Row(3, "АБВГ.301122.ХХХ", "Новая серия"), new FileMessageBook.Row(7, Unknown, "Закрытый код"));

        var preview = File(bytes);

        Assert.True(preview.Ok, preview.Explanation);
        Assert.Equal(0, preview.Rows);
        var reply = ReplyMarkup.Text(preview.Answer);
        Assert.Contains("АБВГ.301121.002", reply, StringComparison.Ordinal);
        Assert.Contains("АБВГ.301122.001", reply, StringComparison.Ordinal);
        Assert.Contains("АБВГ.806121.001", reply, StringComparison.Ordinal);
        Assert.Contains("не найден", reply, StringComparison.Ordinal);
        foreach (var node in EskdCatalog.Chain("301122"))
            Assert.Contains($"{node.Code} — {node.Name}", reply, StringComparison.Ordinal);
        Assert.Equal(1, reply.Split("Подтвердите: Да или Нет", StringSplitOptions.None).Length - 1);
        Assert.Equal(before, Snapshot(_db.Context));
        Assert.False(Text("Да", sender: "1002").Ok);
        Assert.False(Text("Да", channel: "other").Ok);
        Assert.False(Text("Да", session: new object()).Ok);
        Assert.Equal(before, Snapshot(_db.Context));

        var done = Text("Да");
        Assert.True(done.Ok, done.Explanation);
        Assert.Equal(3, done.Rows);
        Assert.Contains("3 из 3", ReplyMarkup.Text(done.Answer), StringComparison.Ordinal);
        Assert.Equal(4, Numbers().Length);
        Assert.False(Text("Да").Ok);
    }

    [Fact]
    public void An_existing_unknown_catalog_series_does_not_ask_for_confirmation_again()
    {
        Seed(code: "806121");
        var result = File(FileMessageBook.Create(new FileMessageBook.Row(2, Unknown)));
        Assert.True(result.Ok, result.Explanation);
        Assert.Equal(1, result.Rows);
        Assert.DoesNotContain("Подтвердите", ReplyMarkup.Text(result.Answer), StringComparison.Ordinal);
        Assert.Equal("АБВГ.806121.002", Values("Документ")[_shape.DesignationFieldId]);
    }

    [Fact]
    public void Repeated_delivery_after_a_new_context_and_session_returns_the_saved_receipt()
    {
        Seed();
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2, Mask, "Первый"), new FileMessageBook.Row(3, Mask, "Второй"));
        var first = File(bytes);
        Assert.True(first.Ok, first.Explanation);
        Assert.Equal(2, first.Rows);
        var before = Snapshot(_db.Context);

        using var reopened = new AppDbContext(_db.Path);
        reopened.ApplyPerformancePragmas();
        var replay = File(bytes, repo: new ConstructorRepository(reopened), session: new object());

        Assert.True(replay.Ok, replay.Explanation);
        Assert.Equal(0, replay.Rows);
        Assert.Equal(first.Answer, replay.Answer);
        Assert.Equal(before, Snapshot(reopened));
    }

    [Fact]
    public void A_duplicate_after_confirmation_replays_success_instead_of_allocating_more_numbers()
    {
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2));
        Assert.Equal(0, File(bytes).Rows);
        var done = Text("Да");
        Assert.True(done.Ok, done.Explanation);
        Assert.Equal(1, done.Rows);
        var before = Snapshot(_db.Context);

        var replay = File(bytes, session: new object());

        Assert.True(replay.Ok, replay.Explanation);
        Assert.Equal(0, replay.Rows);
        Assert.Equal(done.Answer, replay.Answer);
        Assert.Equal(before, Snapshot(_db.Context));
    }

    [Fact]
    public void A_changed_payload_under_a_committed_message_id_is_refused_without_any_write()
    {
        Seed();
        Assert.True(File(FileMessageBook.Create(new FileMessageBook.Row(2, Name: "Первый файл"))).Ok);
        var before = Snapshot(_db.Context);

        var conflict = File(FileMessageBook.Create(new FileMessageBook.Row(2, Name: "Другой файл")));

        Assert.False(conflict.Ok);
        Assert.Equal(0, conflict.Rows);
        Assert.Equal(before, Snapshot(_db.Context));
    }

    [Fact]
    public void Distinct_events_and_sources_are_not_deduplicated_by_file_bytes_alone()
    {
        Seed();
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2));
        Assert.Equal(1, File(bytes).Rows);
        Assert.Equal(1, File(bytes, messageId: "file-2").Rows);
        Assert.Equal(1, File(bytes, sender: "1002").Rows);
        Assert.Equal(1, File(bytes, channel: "other").Rows);
        Assert.Equal(5, Numbers().Length);
        Assert.Equal(5, Numbers().Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_same_source_message_can_target_another_journal_without_reusing_its_receipt()
    {
        Seed();
        var other = JournalStand.Shape(_db.Constructor, JournalStand.Create(_db.Constructor, "Другой журнал"), "Другой журнал");
        JournalStand.AddRow(_db.Constructor, other, "Существующий", "АБВГ.301121.001");
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2));
        Assert.Equal(1, File(bytes).Rows);

        var result = new RegisterMessageAction().Run(new ActionContext(_db.Constructor, 1,
            other.CollectionId, Now,
            new Dictionary<string, string> { [DocRegistryExtension.OrgCodesKey] = "АБВГ" },
            Session: _session, Channel: "file-channel"), new ActionInput(new Dictionary<string, string>
            {
                [RegisterMessageAction.SenderField] = "1001",
                [RegisterMessageAction.MessageIdField] = "file-1",
            }, Bytes: bytes, FileName: "Список.xlsx"));

        Assert.True(result.Ok, result.Explanation);
        Assert.Equal(1, result.Rows);
        Assert.Equal(new[] { "АБВГ.301121.001", "АБВГ.301121.002" },
            DocRegistrar.Designations(_db.Constructor, other).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void An_unreadable_previous_receipt_blocks_re_registration_instead_of_guessing()
    {
        Seed();
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2));
        Assert.Equal(1, File(bytes).Rows);
        var receipt = Assert.Single(_db.Context.DbMeta.Where(item =>
            item.Key.StartsWith("extension-state:doc-registry:file:")));
        receipt.Value = "поврежденная квитанция";
        _db.Context.SaveChanges();
        var before = Snapshot(_db.Context);

        var result = File(bytes, session: new object());

        Assert.False(result.Ok);
        Assert.Equal(0, result.Rows);
        Assert.Equal(before, Snapshot(_db.Context));
    }

    [Fact]
    public void A_failed_validation_does_not_reserve_the_message_id_or_number()
    {
        Seed();
        var before = Snapshot(_db.Context);
        Assert.False(File(FileMessageBook.Create(new FileMessageBook.Row(2, "АБВГ.30112.ХХХ"))).Ok);
        Assert.Equal(before, Snapshot(_db.Context));

        var corrected = File(FileMessageBook.Create(new FileMessageBook.Row(2)));

        Assert.True(corrected.Ok, corrected.Explanation);
        Assert.Equal(1, corrected.Rows);
        Assert.Equal("АБВГ.301121.002", Values("Документ")[_shape.DesignationFieldId]);
    }

    [Fact]
    public void No_and_expiration_leave_no_partial_rows_or_durable_receipt()
    {
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2));
        var before = Snapshot(_db.Context);
        Assert.True(File(bytes).Ok);
        Assert.True(Text("Нет").Ok);
        Assert.Equal(before, Snapshot(_db.Context));
        Assert.True(File(bytes).Ok);
        var expired = Text("Да", minutes: 10);
        Assert.False(expired.Ok);
        Assert.Equal(before, Snapshot(_db.Context));
        Assert.True(File(bytes, minutes: 11).Ok);
        Assert.Equal(1, Text("Да", minutes: 11).Rows);
    }

    [Fact]
    public void Confirmation_rejects_a_changed_series_instead_of_silently_renumbering()
    {
        Seed();
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2, Mask, "Старая серия"), new FileMessageBook.Row(3, Unknown, "Новая серия"));
        Assert.True(File(bytes).Ok);
        Seed(number: 20);
        var before = Snapshot(_db.Context);

        var result = Text("Да");

        Assert.False(result.Ok);
        Assert.Contains("Серия изменилась", ReplyMarkup.Text(result.Answer), StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(_db.Context));
        Assert.DoesNotContain(Numbers(), value => value.StartsWith("АБВГ.806121.", StringComparison.Ordinal));
    }

    [Fact]
    public void Failure_on_the_second_row_rolls_back_rows_history_and_the_receipt()
    {
        Seed();
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2, Mask, "Первый"), new FileMessageBook.Row(3, Mask, "Второй"));
        var before = Snapshot(_db.Context);
        _db.Context.Database.ExecuteSqlRaw("""
            CREATE TRIGGER sb_file_reject_second BEFORE INSERT ON db_field_values
            WHEN NEW.value = 'АБВГ.301121.003'
            BEGIN SELECT RAISE(ABORT, 'file fixture failure'); END;
            """);
        ActionResult? result = null;
        var error = Record.Exception(() => result = File(bytes));
        Assert.True(error is not null || result is { Ok: false });
        Assert.Equal(before, Snapshot(_db.Context));
        _db.Context.Database.ExecuteSqlRaw("DROP TRIGGER sb_file_reject_second");

        var retry = File(bytes);

        Assert.True(retry.Ok, retry.Explanation);
        Assert.Equal(2, retry.Rows);
        Assert.Equal("АБВГ.301121.002", Values("Первый")[_shape.DesignationFieldId]);
        Assert.Equal("АБВГ.301121.003", Values("Второй")[_shape.DesignationFieldId]);
    }

    [Fact]
    public void Failure_to_store_the_receipt_cannot_commit_the_already_written_documents()
    {
        Seed();
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2));
        var before = Snapshot(_db.Context);
        _db.Context.Database.ExecuteSqlRaw("""
            CREATE TRIGGER sb_file_reject_receipt BEFORE INSERT ON db_meta
            WHEN NEW.key LIKE 'extension-state:%:file:%'
            BEGIN SELECT RAISE(ABORT, 'receipt fixture failure'); END;
            """);
        ActionResult? result = null;
        var error = Record.Exception(() => result = File(bytes));
        Assert.True(error is not null || result is { Ok: false });
        Assert.Equal(before, Snapshot(_db.Context));
        _db.Context.Database.ExecuteSqlRaw("DROP TRIGGER sb_file_reject_receipt");

        var retry = File(bytes);

        Assert.True(retry.Ok, retry.Explanation);
        Assert.Equal(1, retry.Rows);
        Assert.Equal("АБВГ.301121.002", Values("Документ")[_shape.DesignationFieldId]);
    }

    [Fact]
    public void A_repeated_pending_delivery_keeps_the_original_confirmation_and_file()
    {
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2, Name: "Исходный"));
        var preview = File(bytes);
        var replay = File(bytes);
        Assert.Equal(preview.Answer, replay.Answer);
        Assert.Equal(0, replay.Rows);
        Assert.False(File(FileMessageBook.Create(new FileMessageBook.Row(2, Name: "Подмена"))).Ok);
        Assert.Empty(Numbers());

        var confirmed = Text("Да");

        Assert.True(confirmed.Ok, confirmed.Explanation);
        Assert.Equal(1, confirmed.Rows);
        Assert.Equal("АБВГ.301121.001", Values("Исходный")[_shape.DesignationFieldId]);
        Assert.DoesNotContain(_db.Constructor.GetRecords(_shape.CollectionId), record => record.Title == "Подмена");
    }

    [Fact]
    public void Pending_file_capacity_is_shared_and_cancelling_releases_the_entire_reservation()
    {
        // Каждая книга укладывается в отдельный лимит, две вместе превышают общий бюджет.
        // Одинаковые маски запрашивают 800 разных документов новой серии, а не один повтор.
        var bytes = FileMessageBook.Create(Enumerable.Range(2, 800)
            .Select(line => new FileMessageBook.Row(line, Name: new string('Д', 3000))).ToArray());
        var before = Snapshot(_db.Context);
        var first = File(bytes, sender: "1001", messageId: "large-1");
        Assert.True(first.Ok, first.Explanation);
        Assert.Equal(0, first.Rows);
        Assert.Contains("Заявка на регистрацию: 800", ReplyMarkup.Text(first.Answer), StringComparison.Ordinal);
        Assert.Contains("АБВГ.301121.800", ReplyMarkup.Text(first.Answer), StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(_db.Context));

        var blocked = File(bytes, sender: "1002", messageId: "large-2");

        Assert.False(blocked.Ok);
        Assert.Equal(0, blocked.Rows);
        Assert.Contains("0 из 800", ReplyMarkup.Text(blocked.Answer), StringComparison.Ordinal);
        Assert.Contains("слишком много данных", ReplyMarkup.Text(blocked.Answer), StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(_db.Context));
        Assert.True(Text("Нет", sender: "1001").Ok);

        var admitted = File(bytes, sender: "1002", messageId: "large-2");

        Assert.True(admitted.Ok, admitted.Explanation);
        Assert.Equal(0, admitted.Rows);
        var reply = ReplyMarkup.Text(admitted.Answer);
        Assert.Contains("Заявка на регистрацию: 800", reply, StringComparison.Ordinal);
        Assert.Contains("АБВГ.301121.001", reply, StringComparison.Ordinal);
        Assert.Contains("АБВГ.301121.800", reply, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(_db.Context));
        Assert.True(Text("Нет", sender: "1002").Ok);
        Assert.Equal(before, Snapshot(_db.Context));
    }

    [Fact]
    public void Attachment_identity_is_required_and_cancelled_processing_cannot_write_a_receipt()
    {
        Seed();
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2));
        var before = Snapshot(_db.Context);
        Assert.False(File(bytes, messageId: "").Ok);
        Assert.Equal(before, Snapshot(_db.Context));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => File(bytes, ct: cancelled.Token));
        Assert.Equal(before, Snapshot(_db.Context));
        Assert.Equal(1, File(bytes).Rows);
    }

    [Fact]
    public void Cancellation_after_the_first_document_rolls_back_the_file_and_receipt()
    {
        Seed();
        var bytes = FileMessageBook.Create(new FileMessageBook.Row(2, Name: "Первый"), new FileMessageBook.Row(3, Name: "Второй"));
        using var cancelled = new CancellationTokenSource();
        ((SqliteConnection)_db.Context.Database.GetDbConnection()).CreateFunction("sb_cancel_file", () =>
        {
            cancelled.Cancel();
            return 1;
        });
        _db.Context.Database.ExecuteSqlRaw("""
            CREATE TRIGGER sb_file_cancel_after_first BEFORE INSERT ON db_field_values
            WHEN NEW.value = 'АБВГ.301121.002'
            BEGIN SELECT sb_cancel_file(); END;
            """);
        var before = Snapshot(_db.Context);

        Assert.Throws<OperationCanceledException>(() => File(bytes, ct: cancelled.Token));

        Assert.Equal(before, Snapshot(_db.Context));
        _db.Context.Database.ExecuteSqlRaw("DROP TRIGGER sb_file_cancel_after_first");
        Assert.Equal(2, File(bytes).Rows);
    }

    [Fact]
    public void A_cell_cannot_inject_message_fields_through_its_line_breaks()
    {
        Seed();
        const string title = "Деталь\nФИО: Подмена Автора Записи";
        var result = File(FileMessageBook.Create(new FileMessageBook.Row(2, Name: title)));
        Assert.True(result.Ok, result.Explanation);
        Assert.Equal("Иванов Иван Иванович", Values(title)[_shape.FioFieldId]);
    }

    private ActionResult File(byte[] bytes, string messageId = "file-1", string sender = "1001",
        string channel = "file-channel", object? session = null, ConstructorRepository? repo = null,
        int minutes = 0, CancellationToken ct = default) =>
        new RegisterMessageAction().Run(Context(repo, session, channel, minutes, ct),
            new ActionInput(new Dictionary<string, string>
            {
                [RegisterMessageAction.SenderField] = sender,
                [RegisterMessageAction.MessageIdField] = messageId,
            }, Bytes: bytes, FileName: "Список.xlsx"));

    private ActionResult Text(string text, string sender = "1001", string channel = "file-channel",
        object? session = null, int minutes = 0) =>
        new RegisterMessageAction().Run(Context(null, session, channel, minutes),
            new ActionInput(new Dictionary<string, string>
            {
                [RegisterMessageAction.TextField] = text,
                [RegisterMessageAction.SenderField] = sender,
            }));

    private ActionContext Context(ConstructorRepository? repo, object? session, string channel,
        int minutes = 0, CancellationToken ct = default) =>
        new(repo ?? _db.Constructor, 1, _shape.CollectionId, Now.AddMinutes(minutes),
            new Dictionary<string, string> { [DocRegistryExtension.OrgCodesKey] = "АБВГ" },
            Session: session ?? _session, Channel: channel, CancellationToken: ct);

    private void Seed(int number = 1, string code = "301121") =>
        JournalStand.AddRow(_db.Constructor, _shape, "Существующий", $"АБВГ.{code}.{number:D3}");

    private Dictionary<int, string> Values(string title) => _db.Constructor.GetFieldValues(
        Assert.Single(_db.Constructor.GetRecords(_shape.CollectionId), record => record.Title == title).Id!.Value);

    private string[] Numbers() => DocRegistrar.Designations(_db.Constructor, _shape).Order(StringComparer.Ordinal).ToArray();

    private static string Snapshot(AppDbContext context) => JsonSerializer.Serialize(new
    {
        Records = context.DbRecords.AsNoTracking().OrderBy(record => record.Id).Select(record => new
            { record.Id, record.CollectionId, record.Title, record.Position, record.CreatedAt, record.UpdatedAt,
              record.CreatedBy, record.UpdatedBy }).ToArray(),
        Values = context.DbFieldValues.AsNoTracking().OrderBy(value => value.RecordId).ThenBy(value => value.FieldId)
            .Select(value => new { value.RecordId, value.FieldId, value.Value }).ToArray(),
        Log = context.ChangeLog.AsNoTracking().OrderBy(change => change.Id).ToArray(),
        Meta = context.DbMeta.AsNoTracking().OrderBy(item => item.Key).Select(item => new { item.Key, item.Value }).ToArray(),
    });
}
