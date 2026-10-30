using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartBase.Extensions;
using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>Одна заявка с несколькими номерами: проверка, подтверждение и запись целиком.</summary>
public sealed class BatchRegistrationTests : IDisposable
{
    private readonly TempDatabase _db = new("message-batch");
    private readonly int _journal;
    private readonly RegistryShape _shape;
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private const string Mask = "АБВГ.301121.ХХХ";
    private const string Unknown = "АБВГ.806121.ХХХ";

    public BatchRegistrationTests()
    {
        _journal = JournalStand.Create(_db.Constructor);
        _shape = JournalStand.Shape(_db.Constructor, _journal);
    }

    public void Dispose() => _db.Dispose();

    private ActionResult Call(string text, string sender = "1001", int minutes = 0,
                              string channel = "bot", string orgs = "АБВГ", object? session = null) =>
        new RegisterMessageAction().Run(new ActionContext(_db.Constructor, 1, _journal,
            Now.AddMinutes(minutes), new Dictionary<string, string>
            {
                [DocRegistryExtension.OrgCodesKey] = orgs,
            }, Session: session, Channel: channel), new ActionInput(new Dictionary<string, string>
            {
                [RegisterMessageAction.TextField] = text,
                [RegisterMessageAction.SenderField] = sender,
            }));

    private static string Request(params string[] numbers) =>
        $"Действие: Регистрация\nНомер: {string.Join(", ", numbers)}\n"
        + "Наименование: Общая документация\nПроект: Общий проект\nФИО: Иванов Иван Иванович";

    private void Seed(int number, string code = "301121", int? execution = null) =>
        JournalStand.AddRow(_db.Constructor, _shape, "Существующий",
            execution is { } e ? $"АБВГ.{code}.{number:D3}-{e:D2}" : $"АБВГ.{code}.{number:D3}");

    private string[] Numbers() => DocRegistrar.Designations(_db.Constructor, _shape).Order(StringComparer.Ordinal).ToArray();

    private string Snapshot() => JsonSerializer.Serialize(new
    {
        Records = _db.Context.DbRecords.AsNoTracking().OrderBy(r => r.Id).Select(r => new
            { r.Id, r.CollectionId, r.Title, r.CreatedAt, r.UpdatedAt, r.CreatedBy, r.UpdatedBy, r.Position }).ToList(),
        Values = _db.Context.DbFieldValues.AsNoTracking().OrderBy(v => v.RecordId).ThenBy(v => v.FieldId)
            .Select(v => new { v.RecordId, v.FieldId, v.Value }).ToList(),
        Log = _db.Context.ChangeLog.AsNoTracking().OrderBy(l => l.Id).ToList(),
    });

    [Fact]
    public void A_mixed_catalog_and_syntax_result_reports_every_item_without_a_pending_confirmation()
    {
        Assert.NotNull(EskdCatalog.Find("301121"));
        Assert.Null(EskdCatalog.Find("806121"));
        Seed(1);
        var before = Snapshot();

        var result = Call(Request(Mask, Unknown, "АБВГ.30112.ХХХ"));

        Assert.False(result.Ok);
        var reply = ReplyMarkup.Text(result.Answer);
        Assert.Contains("Заявка отклонена. Зарегистрировано: 0 из 3.", reply, StringComparison.Ordinal);
        Assert.Contains("АБВГ.301121.ХХХ", reply, StringComparison.Ordinal);
        Assert.Contains("АБВГ.806121.ХХХ", reply, StringComparison.Ordinal);
        Assert.Contains("АБВГ.30112.ХХХ", reply, StringComparison.Ordinal);
        Assert.Contains("не найден", reply, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot());
        Assert.False(Call("Да").Ok);
        Assert.Equal(before, Snapshot());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void A_hard_error_at_any_position_blocks_the_whole_list(int position)
    {
        Seed(1);
        var numbers = new[] { Mask, Mask, Mask };
        numbers[position] = "АБВГ.30112.ХХХ";
        var before = Snapshot();

        var result = Call(Request(numbers));

        Assert.False(result.Ok);
        Assert.Contains("0 из 3", ReplyMarkup.Text(result.Answer), StringComparison.Ordinal);
        Assert.Equal(before, Snapshot());
        Assert.False(Call("Да").Ok);
    }

    [Fact]
    public void Several_bad_items_are_reported_together()
    {
        var result = Call(Request("АБВГ.30112.ХХХ", "АБВГ.30112А.ХХХ", "АБВГ.301121.ХХХ-ХХ"));

        Assert.False(result.Ok);
        var reply = ReplyMarkup.Text(result.Answer);
        Assert.Contains("0 из 3", reply, StringComparison.Ordinal);
        foreach (var bad in new[] { "АБВГ.30112.ХХХ", "АБВГ.30112А.ХХХ", "АБВГ.301121.ХХХ-ХХ" })
            Assert.Contains(bad, reply, StringComparison.Ordinal);
        Assert.Empty(Numbers());
    }

    [Fact]
    public void A_missing_shared_field_prevents_the_whole_batch_and_confirmation()
    {
        var text = Request(Mask, Unknown)
            .Replace("Наименование: Общая документация", "Наименование:", StringComparison.Ordinal)
            .Replace("Проект: Общий проект", "Проект:", StringComparison.Ordinal);
        var result = Call(text);

        Assert.False(result.Ok);
        var reply = ReplyMarkup.Text(result.Answer);
        Assert.Contains("Наименование", reply, StringComparison.Ordinal);
        Assert.Contains("Проект", reply, StringComparison.Ordinal);
        Assert.Empty(Numbers());
        Assert.False(Call("Да").Ok);
    }

    [Fact]
    public void An_excessive_batch_reports_its_actual_count_and_creates_no_confirmation()
    {
        var before = Snapshot();
        var count = MessageRegistration.MaxBatch + 1;

        var result = Call(Request(Enumerable.Repeat(Mask, count).ToArray()));

        Assert.False(result.Ok);
        var reply = ReplyMarkup.Text(result.Answer);
        Assert.Contains($"Зарегистрировано: 0 из {count}.", reply, StringComparison.Ordinal);
        Assert.Contains($"не больше {MessageRegistration.MaxBatch}", reply, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot());
        Assert.False(Call("Да").Ok);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void A_comma_in_shared_fields_keeps_the_single_registration_path_and_response()
    {
        Seed(1);
        var text = Request(Mask)
            .Replace("Общая документация", "Документ, узел", StringComparison.Ordinal)
            .Replace("Общий проект", "Машина, корпус", StringComparison.Ordinal);

        var result = Call(text);

        Assert.True(result.Ok, result.Explanation);
        Assert.Equal(1, result.Rows);
        Assert.StartsWith("Зарегистрировано\nНомер: АБВГ.301121.002", ReplyMarkup.Text(result.Answer), StringComparison.Ordinal);
        Assert.Contains("Наименование: Документ, узел", ReplyMarkup.Text(result.Answer), StringComparison.Ordinal);
        var record = Assert.Single(_db.Constructor.GetRecords(_journal), r => r.Title == "Документ, узел");
        Assert.Equal("Машина, корпус", _db.Constructor.GetFieldValues(record.Id!.Value)[_shape.ProjectFieldId]);
        Assert.Equal(2, Numbers().Length);
    }

    [Fact]
    public void Batch_previews_and_success_escape_shared_fields_without_changing_stored_values()
    {
        var text = Request(Mask, Unknown)
            .Replace("Общая документация", "Документ & <левый>", StringComparison.Ordinal)
            .Replace("Общий проект", "Машина & <узел>", StringComparison.Ordinal);

        var preview = Call(text);

        Assert.True(preview.Ok, preview.Explanation);
        Assert.Contains("Документ &amp; &lt;левый&gt;", preview.Answer, StringComparison.Ordinal);
        Assert.Contains("Машина &amp; &lt;узел&gt;", preview.Answer, StringComparison.Ordinal);
        Assert.Empty(Numbers());
        var done = Call("Да");
        Assert.True(done.Ok, done.Explanation);
        Assert.Contains("Документ &amp; &lt;левый&gt;", done.Answer, StringComparison.Ordinal);
        Assert.Contains("Машина &amp; &lt;узел&gt;", done.Answer, StringComparison.Ordinal);
        var rows = _db.Constructor.GetRecords(_journal);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal("Документ & <левый>", row.Title);
            Assert.Equal("Машина & <узел>", _db.Constructor.GetFieldValues(row.Id!.Value)[_shape.ProjectFieldId]);
        });
    }

    [Fact]
    public void A_malformed_item_is_escaped_in_the_rejection_status()
    {
        var result = Call(Request(Mask, "АБВГ.30112.ХХХ<&>"));

        Assert.False(result.Ok);
        Assert.Contains("АБВГ.30112.ХХХ&lt;&amp;&gt;", result.Answer, StringComparison.Ordinal);
        Assert.Contains("АБВГ.30112.ХХХ<&>", ReplyMarkup.Text(result.Answer), StringComparison.Ordinal);
        Assert.Empty(Numbers());
        Assert.False(Call("Да").Ok);
    }

    [Fact]
    public void Existing_series_are_registered_together_without_an_extra_confirmation()
    {
        Seed(1);
        Seed(1, "301122");

        var result = Call(Request(Mask, "АБВГ.301122.ХХХ"));

        Assert.True(result.Ok, result.Explanation);
        Assert.Equal(2, result.Rows);
        Assert.Contains("Зарегистрировано: 2 из 2.", ReplyMarkup.Text(result.Answer), StringComparison.Ordinal);
        Assert.Equal(new[] { "АБВГ.301121.001", "АБВГ.301121.002", "АБВГ.301122.001", "АБВГ.301122.002" }, Numbers());
        Assert.False(Call("Да").Ok);
        var created = _db.Constructor.GetRecords(_journal).Where(r => r.Title == "Общая документация").ToArray();
        Assert.Equal(2, created.Length);
        foreach (var record in created)
        {
            var values = _db.Constructor.GetFieldValues(record.Id!.Value);
            Assert.Equal("Общий проект", values[_shape.ProjectFieldId]);
            Assert.Equal("Иванов Иван Иванович", values[_shape.FioFieldId]);
        }
    }

    [Fact]
    public void Repeated_masks_allocate_distinct_first_free_numbers_in_order()
    {
        Seed(1);
        Seed(3);

        var result = Call(Request(Mask, Mask, Mask));

        Assert.True(result.Ok, result.Explanation);
        Assert.Equal(3, result.Rows);
        Assert.Equal(Enumerable.Range(1, 5).Select(n => $"АБВГ.301121.{n:D3}"), Numbers());
        var reply = ReplyMarkup.Text(result.Answer);
        var first = reply.IndexOf("АБВГ.301121.002", StringComparison.Ordinal);
        var second = reply.IndexOf("АБВГ.301121.004", StringComparison.Ordinal);
        var third = reply.IndexOf("АБВГ.301121.005", StringComparison.Ordinal);
        Assert.True(first >= 0 && first < second && second < third, reply);
    }

    [Fact]
    public void A_new_series_holds_existing_series_rows_until_the_collective_yes()
    {
        Seed(1);
        var before = Snapshot();

        var preview = Call(Request(Mask, Unknown, Mask));

        Assert.True(preview.Ok, preview.Explanation);
        Assert.Equal(0, preview.Rows);
        var reply = ReplyMarkup.Text(preview.Answer);
        Assert.Contains("Заявка на регистрацию: 3", reply, StringComparison.Ordinal);
        Assert.Contains("АБВГ.301121.002", reply, StringComparison.Ordinal);
        Assert.Contains("АБВГ.806121.001", reply, StringComparison.Ordinal);
        Assert.Contains("АБВГ.301121.003", reply, StringComparison.Ordinal);
        Assert.Contains("Возможно, он относится к закрытому классификатору", reply, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot());

        var result = Call("Да");

        Assert.True(result.Ok, result.Explanation);
        Assert.Equal(3, result.Rows);
        Assert.Contains("Зарегистрировано: 3 из 3.", ReplyMarkup.Text(result.Answer), StringComparison.Ordinal);
        Assert.Equal(new[] { "АБВГ.301121.001", "АБВГ.301121.002", "АБВГ.301121.003", "АБВГ.806121.001" }, Numbers());
        Assert.False(Call("Да").Ok);
    }

    [Fact]
    public void Several_new_series_show_each_catalog_chain_and_one_confirmation_prompt()
    {
        var preview = Call(Request(Mask, "АБВГ.301122.ХХХ", Unknown));

        Assert.True(preview.Ok, preview.Explanation);
        var reply = ReplyMarkup.Text(preview.Answer);
        foreach (var code in new[] { "301121", "301122" })
            foreach (var node in EskdCatalog.Chain(code))
                Assert.Contains($"{node.Code} — {node.Name}", reply, StringComparison.Ordinal);
        Assert.Contains("806121", reply, StringComparison.Ordinal);
        Assert.Equal(1, reply.Split("Подтвердите: Да или Нет", StringSplitOptions.None).Length - 1);
        Assert.Empty(Numbers());
        Assert.True(Call("Да").Ok);
        Assert.Equal(3, Numbers().Length);
    }

    [Fact]
    public void A_catalog_absent_existing_series_does_not_request_confirmation_again()
    {
        Seed(1);
        Seed(1, "806121");

        var result = Call(Request(Mask, Unknown));

        Assert.True(result.Ok, result.Explanation);
        Assert.Equal(2, result.Rows);
        var reply = ReplyMarkup.Text(result.Answer);
        Assert.DoesNotContain("Подтвердите", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("не найден", reply, StringComparison.Ordinal);
        Assert.Equal(4, Numbers().Length);
    }

    [Fact]
    public void No_cancels_the_entire_batch_without_history_changes()
    {
        Seed(1);
        var before = Snapshot();
        Assert.True(Call(Request(Mask, Unknown)).Ok);

        var result = Call("Нет");

        Assert.True(result.Ok, result.Explanation);
        Assert.Equal("Операция отменена", ReplyMarkup.Text(result.Answer));
        Assert.Equal(before, Snapshot());
        Assert.False(Call("Да").Ok);
    }

    [Fact]
    public void Expiration_does_not_register_any_item()
    {
        Assert.True(Call(Request(Mask, Unknown)).Ok);
        var before = Snapshot();

        var result = Call("Да", minutes: 10);

        Assert.False(result.Ok);
        Assert.Contains("срок истек", ReplyMarkup.Text(result.Answer), StringComparison.Ordinal);
        Assert.Equal(before, Snapshot());
        Assert.False(Call("Да").Ok);
    }

    [Fact]
    public void The_sender_and_source_of_the_whole_batch_must_match()
    {
        Assert.True(Call(Request(Mask, Unknown)).Ok);

        Assert.False(Call("Да", sender: "1002").Ok);
        Assert.False(Call("Да", channel: "other").Ok);
        Assert.Empty(Numbers());
        Assert.True(Call("Да").Ok);
        Assert.Equal(2, Numbers().Length);
    }

    [Fact]
    public void A_new_session_does_not_inherit_a_previous_batch_confirmation()
    {
        var session = new object();
        Assert.True(Call(Request(Mask, Unknown), session: session).Ok);

        Assert.False(Call("Да", session: new object()).Ok);
        Assert.Empty(Numbers());
        Assert.True(Call("Да", session: session).Ok);
        Assert.Equal(2, Numbers().Length);
    }

    [Fact]
    public void A_new_message_cannot_replace_the_pending_batch()
    {
        Assert.True(Call(Request(Mask, Unknown)).Ok);

        var blocked = Call(Request(Mask, Mask, Mask));

        Assert.False(blocked.Ok);
        Assert.Empty(Numbers());
        Assert.True(Call("Да").Ok);
        Assert.Equal(new[] { "АБВГ.301121.001", "АБВГ.806121.001" }, Numbers());
    }

    [Theory]
    [InlineData("301121")]
    [InlineData("806121")]
    public void A_change_to_any_relevant_series_refuses_the_whole_confirmed_batch(string changedCode)
    {
        Seed(1);
        Assert.True(Call(Request(Mask, Unknown)).Ok);
        // Меняется серия, но ни один показанный номер не занят: повторная проверка writer
        // не заменяет проверку всего подтвержденного снимка.
        Seed(20, changedCode);
        var before = Snapshot();

        var result = Call("Да");

        Assert.False(result.Ok);
        Assert.Contains("Серия изменилась", ReplyMarkup.Text(result.Answer), StringComparison.Ordinal);
        Assert.Equal(before, Snapshot());
        Assert.False(Call("Да").Ok);
    }

    [Fact]
    public void A_change_to_an_unrelated_series_does_not_reject_the_batch()
    {
        Assert.True(Call(Request(Mask, Unknown)).Ok);
        Seed(1, "301122");

        var result = Call("Да");

        Assert.True(result.Ok, result.Explanation);
        Assert.Equal(2, result.Rows);
        Assert.Equal(3, Numbers().Length);
    }

    [Fact]
    public void Changed_organization_settings_block_the_entire_confirmation()
    {
        Assert.True(Call(Request(Mask, Unknown)).Ok);
        var before = Snapshot();

        var result = Call("Да", orgs: "ГМБВ");

        Assert.False(result.Ok);
        Assert.Equal(before, Snapshot());
        Assert.False(Call("Да").Ok);
    }

    [Fact]
    public void Repeated_execution_masks_fill_holes_without_changing_the_base_document()
    {
        Seed(14);
        Seed(14, execution: 1);
        Seed(14, execution: 3);

        var result = Call(Request("АБВГ.301121.014-ХХ", "АБВГ.301121.014-ХХ"));

        Assert.True(result.Ok, result.Explanation);
        Assert.Equal(2, result.Rows);
        Assert.Equal(new[] { "АБВГ.301121.014", "АБВГ.301121.014-01", "АБВГ.301121.014-02",
                            "АБВГ.301121.014-03", "АБВГ.301121.014-04" }, Numbers());
    }

    [Fact]
    public void An_execution_mask_keeps_the_existing_rule_that_a_base_row_is_not_required()
    {
        Seed(1);

        var result = Call(Request("АБВГ.301121.014-ХХ", "АБВГ.301121.014-ХХ"));

        Assert.True(result.Ok, result.Explanation);
        Assert.Equal(new[] { "АБВГ.301121.001", "АБВГ.301121.014-01", "АБВГ.301121.014-02" }, Numbers());
    }

    [Fact]
    public void A_series_with_only_one_free_number_cannot_partially_accept_two_masks()
    {
        _db.Constructor.InCollectionWriteTransaction(_journal, () =>
        {
            foreach (var number in Enumerable.Range(1, 998)) Seed(number);
            return 0;
        });
        var before = Snapshot();

        var result = Call(Request(Mask, Mask));

        Assert.False(result.Ok);
        Assert.Contains("0 из 2", ReplyMarkup.Text(result.Answer), StringComparison.Ordinal);
        Assert.Equal(before, Snapshot());
        Assert.DoesNotContain("АБВГ.301121.999", Numbers());
    }

    [Fact]
    public void A_document_with_only_one_free_execution_cannot_partially_accept_the_batch()
    {
        _db.Constructor.InCollectionWriteTransaction(_journal, () =>
        {
            foreach (var execution in Enumerable.Range(1, 98)) Seed(14, execution: execution);
            return 0;
        });
        var before = Snapshot();

        var result = Call(Request(Mask, "АБВГ.301121.014-ХХ", "АБВГ.301121.014-ХХ"));

        Assert.False(result.Ok);
        Assert.Contains("0 из 3", ReplyMarkup.Text(result.Answer), StringComparison.Ordinal);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void A_database_failure_on_the_second_number_rolls_back_rows_fields_and_history()
    {
        Seed(1);
        var before = Snapshot();
        _db.Context.Database.ExecuteSqlRaw("""
            CREATE TRIGGER registration_batch_fault BEFORE INSERT ON db_field_values
            WHEN NEW.Value = 'АБВГ.301121.003'
            BEGIN SELECT RAISE(ABORT, 'controlled batch registration fault'); END;
            """);
        ActionResult? result = null;
        Exception? failure;
        try { failure = Record.Exception(() => result = Call(Request(Mask, Mask))); }
        finally { _db.Context.Database.ExecuteSqlRaw("DROP TRIGGER registration_batch_fault"); }

        Assert.True(failure is not null || result is { Ok: false }, "Сбой записи не должен превращаться в успех.");
        if (failure is not null) Assert.Contains("controlled batch registration fault", failure.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, Snapshot());
        // После отказа те же номера свободны, а контекст не хранит откаченные записи.
        var retry = Call(Request(Mask, Mask));
        Assert.True(retry.Ok, retry.Explanation);
        Assert.Equal(new[] { "АБВГ.301121.001", "АБВГ.301121.002", "АБВГ.301121.003" }, Numbers());
    }
}
