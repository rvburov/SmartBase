using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Core.Models;
using SmartBase.Extensions;
using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>Настоящий журнал: чтение, отказ без записи и одноразовые подтверждения правки.</summary>
public sealed class DocumentMessageTests : IDisposable
{
    private readonly TempDatabase _db = new("doc-messages");
    private readonly int _journal;
    private readonly RegistryShape _shape;
    private readonly int _id;
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private const string Number = "АБВГ.301121.001";

    public DocumentMessageTests()
    {
        _journal = JournalStand.Create(_db.Constructor);
        _shape = JournalStand.Shape(_db.Constructor, _journal);
        var done = DocRegistrar.Register(_db.Constructor, _shape,
            new RegistrationInput("Кронштейн", "АБВГ", "301121", "Иванов Иван Иванович", "Машина",
                                  Accounting: "Учтен", FirstUse: "Первое применение", Summary: "Стальной"),
            Now.LocalDateTime);
        _id = done.RecordId;
    }

    public void Dispose() => _db.Dispose();

    private ActionResult Call(string text, string sender = "1001", DateTimeOffset? at = null,
                              int? journal = null, int dbId = 1, string senders = "1001 Иванов; 1002 Петров",
                              ConstructorRepository? repo = null) =>
        new RegisterMessageAction().Run(new ActionContext(repo ?? _db.Constructor, dbId, journal ?? _journal,
            at ?? Now, new Dictionary<string, string>
            {
                [DocRegistryExtension.OrgCodesKey] = "АБВГ",
                [DocRegistryExtension.SendersKey] = senders,
            }), new ActionInput(new Dictionary<string, string>
            {
                [RegisterMessageAction.TextField] = text,
                [RegisterMessageAction.SenderField] = sender,
            }));

    private static string Request(string action, string number = Number) => $"Действие: {action}\nНомер: {number}";

    [Fact]
    public void View_returns_every_journal_column_without_a_confirmation()
    {
        var before = _db.Constructor.GetFieldValues(_id);
        var result = Call(Request("Просмотр"));
        Assert.True(result.Ok, result.Explanation);
        foreach (var value in new[] { Number, "Кронштейн", "Машина", "Иванов Иван Иванович",
                                     "Дата регистрации:", "Учтен", "Первое применение", "Стальной" })
            Assert.Contains(value, result.Answer, StringComparison.Ordinal);
        Assert.DoesNotContain("Подтвердите", result.Answer, StringComparison.Ordinal);
        Assert.Equal(before, _db.Constructor.GetFieldValues(_id));
        Assert.False(Call("Да").Ok);
    }

    [Theory]
    [InlineData("да нет")]
    [InlineData("д а")]
    [InlineData("да, удалите")]
    [InlineData("не да")]
    public void Confirmation_requires_a_whole_answer_and_does_not_guess_intent(string answer)
    {
        Assert.True(Call(Request("Удаление")).Ok);
        Assert.False(Call(answer).Ok);
        Assert.NotNull(_db.Constructor.GetRecord(_id));
        Assert.Equal("Операция отменена", Call("нет").Answer);
        Assert.NotNull(_db.Constructor.GetRecord(_id));
    }

    [Theory]
    [InlineData("Да")]
    [InlineData("Нет")]
    public void Deletion_waits_for_the_same_sender_and_is_consumed_once(string answer)
    {
        var result = Call(Request("Удаление"));
        Assert.True(result.Ok, result.Explanation);
        Assert.Equal(0, result.Rows);
        Assert.Contains("Подтвердите удаление", result.Answer, StringComparison.Ordinal);
        Assert.Contains("Кронштейн", result.Answer, StringComparison.Ordinal);
        Assert.NotNull(_db.Constructor.GetRecord(_id));
        Assert.False(Call("Да", sender: "1002").Ok);
        Assert.NotNull(_db.Constructor.GetRecord(_id));
        var confirmed = Call(answer);
        Assert.True(confirmed.Ok, confirmed.Explanation);
        Assert.Equal(answer == "Да" ? 1 : 0, confirmed.Rows);
        Assert.Equal(answer == "Да", _db.Constructor.GetRecord(_id) is null);
        Assert.False(Call("Да").Ok);
    }

    [Theory]
    [InlineData("Да")]
    [InlineData("Нет")]
    public void Editing_previews_old_and_new_values_and_preserves_the_number_and_date(string answer)
    {
        var before = _db.Constructor.GetFieldValues(_id);
        var result = Call(Request("Изменение") + "\nНаименование: Опора\nПроект: Станок\nФИО: Петров Петр Петрович");
        Assert.True(result.Ok, result.Explanation);
        Assert.Contains("Наименование: Кронштейн → Опора", result.Answer, StringComparison.Ordinal);
        Assert.Contains("Проект: Машина → Станок", result.Answer, StringComparison.Ordinal);
        Assert.Equal("Кронштейн", _db.Constructor.GetRecord(_id)!.Title);
        Assert.Equal(before, _db.Constructor.GetFieldValues(_id));
        Assert.True(Call(answer).Ok);
        Assert.Equal(answer == "Да" ? "Опора" : "Кронштейн", _db.Constructor.GetRecord(_id)!.Title);
        var after = _db.Constructor.GetFieldValues(_id);
        Assert.Equal(before[_shape.DesignationFieldId], after[_shape.DesignationFieldId]);
        Assert.Equal(before[_shape.DateFieldId], after[_shape.DateFieldId]);
        Assert.Equal(before[_shape.AccountingFieldId], after[_shape.AccountingFieldId]);
        Assert.Equal(answer == "Да" ? "Станок" : "Машина", after[_shape.ProjectFieldId]);
        Assert.Equal(answer == "Да" ? "Петров Петр Петрович" : "Иванов Иван Иванович", after[_shape.FioFieldId]);
    }

    /// <summary>ФИО при изменении ложится тем же видом, что при регистрации: слова через один пробел.</summary>
    [Fact]
    public void An_edited_full_name_is_collapsed_like_a_registered_one()
    {
        var preview = Call(Request("Изменение") + "\nФИО: Петров   Петр \t Петрович");
        Assert.True(preview.Ok, preview.Explanation);
        Assert.Contains("ФИО: Иванов Иван Иванович → Петров Петр Петрович", preview.Answer, StringComparison.Ordinal);
        Assert.True(Call("Да").Ok);
        Assert.Equal("Петров Петр Петрович", _db.Constructor.GetFieldValues(_id)[_shape.FioFieldId]);

        var same = Call(Request("Изменение") + "\nФИО: Петров  Петр  Петрович");
        Assert.False(same.Ok);
        Assert.Contains("Изменений нет", same.Answer, StringComparison.Ordinal);
    }

    [Fact]
    public void A_patch_does_not_require_or_clear_other_fields()
    {
        Assert.True(Call(Request("Изменение") + "\nПроект: Станок").Ok);
        Assert.True(Call("Да").Ok);
        Assert.Equal("Кронштейн", _db.Constructor.GetRecord(_id)!.Title);
        Assert.Equal("Иванов Иван Иванович", _db.Constructor.GetFieldValues(_id)[_shape.FioFieldId]);
        Assert.Equal("Станок", _db.Constructor.GetFieldValues(_id)[_shape.ProjectFieldId]);
    }

    [Theory]
    [InlineData("Просмотр")]
    [InlineData("Удаление")]
    [InlineData("Изменение")]
    public void Every_action_checks_the_sender_allowlist(string action)
    {
        var result = Call(Request(action), sender: "9999");
        Assert.False(result.Ok);
        Assert.Equal(RegisterMessageAction.NotListed, result.Answer);
        Assert.NotNull(_db.Constructor.GetRecord(_id));
    }

    [Fact]
    public void Access_is_checked_again_before_confirmation()
    {
        Assert.True(Call(Request("Удаление")).Ok);
        Assert.False(Call("Да", senders: "1002 Петров").Ok);
        Assert.NotNull(_db.Constructor.GetRecord(_id));
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(1, true)]
    public void A_confirmation_cannot_cross_a_database_or_collection(int dbId, bool otherCollection)
    {
        Assert.True(Call(Request("Удаление")).Ok);
        var journal = otherCollection ? JournalStand.Create(_db.Constructor, "Другой журнал") : _journal;
        Assert.False(Call("Да", dbId: dbId, journal: journal).Ok);
        Assert.NotNull(_db.Constructor.GetRecord(_id));
        Assert.True(Call("Да").Ok);
        Assert.Null(_db.Constructor.GetRecord(_id));
    }

    [Fact]
    public void Reopening_the_repository_does_not_restore_a_pending_confirmation()
    {
        Assert.True(Call(Request("Удаление")).Ok);
        using var neighbour = new AppDbContext(_db.Path);
        Assert.False(Call("Да", repo: new ConstructorRepository(neighbour)).Ok);
        Assert.NotNull(_db.Constructor.GetRecord(_id));
    }

    [Fact]
    public void A_pending_request_cannot_be_replaced_accidentally()
    {
        Assert.True(Call(Request("Удаление")).Ok);
        Assert.False(Call(Request("Изменение") + "\nПроект: Станок").Ok);
        Assert.False(Call("может быть").Ok);
        Assert.True(Call(Request("Просмотр")).Ok);
        Assert.True(Call("Нет").Ok);
        Assert.Equal("Машина", _db.Constructor.GetFieldValues(_id)[_shape.ProjectFieldId]);
    }

    [Fact]
    public void Expired_confirmation_does_not_write_and_a_new_request_is_possible()
    {
        Assert.True(Call(Request("Удаление")).Ok);
        var result = Call("Да", at: Now.AddMinutes(10));
        Assert.False(result.Ok);
        Assert.Contains("срок истек", result.Answer, StringComparison.Ordinal);
        Assert.NotNull(_db.Constructor.GetRecord(_id));
        Assert.True(Call(Request("Удаление"), at: Now.AddMinutes(11)).Ok);
        Assert.True(Call("Нет", at: Now.AddMinutes(11)).Ok);
    }

    [Theory]
    [InlineData("Просмотр", "")]
    [InlineData("Удаление", "АБВГ.301121.ХХХ")]
    [InlineData("Просмотр", "АБВГ.301121.1")]
    [InlineData("Просмотр", "АБВГ.301121.000")]
    [InlineData("Просмотр", "АБВГ.301121.001-00")]
    [InlineData("Просмотр", "АБВГ.301121.001-01лишнее")]
    public void Invalid_numbers_are_refused_without_a_pending_operation(string action, string number)
    {
        var result = Call(Request(action, number));
        Assert.False(result.Ok);
        Assert.StartsWith("Номер:", result.Answer, StringComparison.Ordinal);
        Assert.False(Call("Да").Ok);
        Assert.NotNull(_db.Constructor.GetRecord(_id));
    }

    [Theory]
    [InlineData("Просмотр")]
    [InlineData("Удаление")]
    [InlineData("Изменение")]
    public void A_missing_document_is_reported(string action)
    {
        var result = Call(Request(action, "АБВГ.301121.999") + (action == "Изменение" ? "\nПроект: Станок" : ""));
        Assert.False(result.Ok);
        Assert.Contains("не найден", result.Answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_designations_are_never_chosen_arbitrarily()
    {
        JournalStand.AddRow(_db.Constructor, _shape, "Дубликат", Number);
        var result = Call(Request("Удаление"));
        Assert.False(result.Ok);
        Assert.Contains("несколько записей", result.Answer, StringComparison.Ordinal);
        Assert.False(Call("Да").Ok);
        Assert.Equal(2, _db.Constructor.GetRecords(_journal).Count);
    }

    [Fact]
    public void All_patch_errors_are_returned_and_nothing_is_written()
    {
        var result = Call(Request("Изменение") + "\nНаименование: \nПроект: \nФИО: Иванов И.И.\nНовый номер: АБВГ.301121.002");
        Assert.False(result.Ok);
        foreach (var field in new[] { "Наименование:", "Проект:", "ФИО:", "Новый номер:" })
            Assert.Contains(field, result.Answer, StringComparison.Ordinal);
        Assert.False(Call("Да").Ok);
        Assert.Equal("Кронштейн", _db.Constructor.GetRecord(_id)!.Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\nНаименование: Кронштейн")]
    public void Empty_or_unchanged_patches_do_not_ask_for_confirmation(string fields)
    {
        Assert.False(Call(Request("Изменение") + fields).Ok);
        Assert.False(Call("Да").Ok);
    }

    [Theory]
    [InlineData("Удаление", true)]
    [InlineData("Удаление", false)]
    [InlineData("Изменение", true)]
    [InlineData("Изменение", false)]
    public void A_neighbour_edit_invalidates_the_confirmation_even_with_a_warm_tracker(string action, bool title)
    {
        Assert.True(Call(Request(action) + (action == "Изменение" ? "\nПроект: Станок" : "")).Ok);
        using (var neighbour = new AppDbContext(_db.Path))
        {
            var repo = new ConstructorRepository(neighbour);
            if (title) repo.SetRecordTitle(_id, "Правка соседа");
            else repo.SetFieldValue(_id, _shape.SummaryFieldId, "Правка соседа");
        }
        var result = Call("Да");
        Assert.False(result.Ok);
        Assert.Contains("изменился", result.Answer, StringComparison.Ordinal);
        Assert.NotNull(_db.Constructor.GetRecord(_id));
        Assert.Equal("Машина", _db.Constructor.GetFieldValuesBatch([_id])[_id][_shape.ProjectFieldId]);
        Assert.False(Call("Да").Ok);
    }

    [Fact]
    public void A_deleted_or_replaced_record_cannot_be_confirmed()
    {
        Assert.True(Call(Request("Удаление")).Ok);
        _db.Constructor.DeleteRecord(_id);
        JournalStand.AddRow(_db.Constructor, _shape, "Другая запись", Number);
        Assert.False(Call("Да").Ok);
        Assert.Single(_db.Constructor.GetRecords(_journal));
    }

    [Fact]
    public void Executions_are_looked_up_exactly_and_markup_is_accepted()
    {
        JournalStand.AddRow(_db.Constructor, _shape, "Исполнение", Number + "-01");
        var result = Call("<p>Действие: Просмотр</p><p>Номер: АБВГ.301121.001-01</p>");
        Assert.True(result.Ok);
        Assert.Contains("Наименование: Исполнение", result.Answer, StringComparison.Ordinal);
        Assert.DoesNotContain("Кронштейн", result.Answer, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lowercase_existing_designation_is_found_by_its_canonical_number()
    {
        _db.Constructor.SetFieldValue(_id, _shape.DesignationFieldId, Number.ToLowerInvariant());
        Assert.True(Call(Request("Просмотр")).Ok);
        Assert.True(Call(Request("Удаление")).Ok);
        Assert.True(Call("Да").Ok);
        Assert.Null(_db.Constructor.GetRecord(_id));
    }

    [Fact]
    public void A_failed_compound_write_rolls_back_the_record_values_and_audit()
    {
        var before = _db.Constructor.GetFieldValues(_id);
        var auditCount = _db.Context.ChangeLog.Count();
        Assert.Throws<InvalidOperationException>(() => _db.Constructor.InWriteTransaction<int>(() =>
        {
            _db.Constructor.SetRecordTitle(_id, "Не сохранять");
            _db.Constructor.SetFieldValue(_id, _shape.ProjectFieldId, "Не сохранять");
            throw new InvalidOperationException("обрыв составной записи");
        }));
        Assert.Equal("Кронштейн", _db.Constructor.GetRecord(_id)!.Title);
        Assert.Equal(before, _db.Constructor.GetFieldValues(_id));
        Assert.Equal(auditCount, _db.Context.ChangeLog.Count());
    }

    [Theory]
    [InlineData("Просмотр", false)]
    [InlineData("Удаление", false)]
    [InlineData("Изменение", false)]
    [InlineData("Удаление", true)]
    [InlineData("Изменение", true)]
    public void Database_permissions_are_checked_before_preview_and_again_before_write(string action, bool afterPreview)
    {
        var request = Request(action) + (action == "Изменение" ? "\nПроект: Станок" : "");
        if (afterPreview) Assert.True(Call(request).Ok);
        var userId = _db.Permissions.CurrentUserId;
        _db.Permissions.SetMask(userId, null, (int)(action == "Просмотр" ? PermAction.None : PermAction.ViewCollection));
        var me = _db.Context.DbUsers.Single(u => u.Id == userId);
        me.IsAdmin = false;
        _db.Context.SaveChanges();
        _db.Permissions.Reload(notify: false);
        var result = Call(afterPreview ? "Да" : request);
        Assert.False(result.Ok);
        Assert.Contains("Недостаточно прав", result.Answer, StringComparison.Ordinal);
        Assert.NotNull(_db.Constructor.GetRecord(_id));
        Assert.Equal("Машина", _db.Constructor.GetFieldValues(_id)[_shape.ProjectFieldId]);
    }

    [Fact]
    public void Registration_without_rights_to_fill_values_does_not_leave_a_partial_record()
    {
        var userId = _db.Permissions.CurrentUserId;
        _db.Permissions.SetMask(userId, null, (int)PermAction.CreateRecord);
        _db.Context.DbUsers.Single(u => u.Id == userId).IsAdmin = false;
        _db.Context.SaveChanges();
        _db.Permissions.Reload(notify: false);
        var result = Call("Действие: Регистрация\nНомер: АБВГ.301121.ХХХ\nНаименование: Опора\nПроект: Станок\nФИО: Иванов Иван Иванович");
        Assert.False(result.Ok);
        Assert.Contains("Недостаточно прав", result.Answer, StringComparison.Ordinal);
        Assert.Single(_db.Constructor.GetRecords(_journal));
    }
}
