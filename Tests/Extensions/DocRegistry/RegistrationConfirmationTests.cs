using SmartBase.Core.Models;
using SmartBase.Extensions;
using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>Регистрация первого номера и кода вне справочника: ни одной записи до согласия.</summary>
public sealed class RegistrationConfirmationTests : IDisposable
{
    private readonly TempDatabase _db = new("registration-confirm");
    private readonly int _journal;
    private readonly RegistryShape _shape;
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    public RegistrationConfirmationTests()
    {
        _journal = JournalStand.Create(_db.Constructor);
        _shape = JournalStand.Shape(_db.Constructor, _journal);
    }
    public void Dispose() => _db.Dispose();

    private ActionResult Call(string text, string sender = "1001", int minutes = 0,
                              string channel = "bot", string orgs = "АБВГ", string senders = "1001;1002") =>
        new RegisterMessageAction().Run(new ActionContext(_db.Constructor, 1, _journal,
            Now.AddMinutes(minutes), new Dictionary<string, string>
            {
                [DocRegistryExtension.OrgCodesKey] = orgs,
                [DocRegistryExtension.SendersKey] = senders,
            }, Channel: channel), new ActionInput(new Dictionary<string, string>
            {
                [RegisterMessageAction.TextField] = text,
                [RegisterMessageAction.SenderField] = sender,
            }));

    private static string Request(string tail = "001", string code = "301121") =>
        $"Действие: Регистрация\nНомер: АБВГ.{code}.{tail}\nНаименование: Кронштейн\nПроект: Машина\nФИО: Иванов Иван Иванович";

    private void Seed(int number, string code = "301121", string org = "АБВГ") =>
        DocRegistrar.Register(_db.Constructor, _shape,
            new RegistrationInput("Существующий", org, code, "Иванов Иван Иванович", "Машина", Number: number),
            Now.LocalDateTime);

    [Theory]
    [InlineData("001")]
    [InlineData("ХХХ")]
    public void First_number_shows_the_actual_catalog_chain_and_waits(string tail)
    {
        var preview = Call(Request(tail));
        Assert.True(preview.Ok, preview.Explanation);
        Assert.Equal(0, preview.Rows);
        Assert.Contains("АБВГ.301121.001", ReplyMarkup.Text(preview.Answer), StringComparison.Ordinal);
        Assert.Contains("Это первый номер", ReplyMarkup.Text(preview.Answer), StringComparison.Ordinal);
        foreach (var node in EskdCatalog.Chain("301121"))
            Assert.Contains($"{node.Code} — {node.Name}", ReplyMarkup.Text(preview.Answer), StringComparison.Ordinal);
        Assert.Empty(_db.Constructor.GetRecords(_journal));
        Assert.True(Call("Инструкция").Ok);
        var done = Call("да");
        Assert.True(done.Ok, done.Explanation);
        Assert.StartsWith("Зарегистрировано\nНомер: АБВГ.301121.001", ReplyMarkup.Text(done.Answer), StringComparison.Ordinal);
        Assert.Single(_db.Constructor.GetRecords(_journal));
        Assert.False(Call("Да").Ok);
    }

    [Theory]
    [InlineData("001")]
    [InlineData("ХХХ")]
    public void Unknown_code_requires_confirmation_without_asserting_that_it_is_closed(string tail)
    {
        Assert.Null(EskdCatalog.Find("000000"));
        var preview = Call(Request(tail, "000000"));
        Assert.True(preview.Ok, preview.Explanation);
        Assert.Contains("Код классификации не найден в доступном справочнике ЕСКД", ReplyMarkup.Text(preview.Answer), StringComparison.Ordinal);
        Assert.Contains("Возможно, он относится к закрытому классификатору", ReplyMarkup.Text(preview.Answer), StringComparison.Ordinal);
        Assert.Empty(_db.Constructor.GetRecords(_journal));
        Assert.True(Call("Да").Ok);
        Assert.Single(_db.Constructor.GetRecords(_journal));
        var second = Call(Request("ХХХ", "000000"));
        Assert.StartsWith("Зарегистрировано\nНомер: АБВГ.000000.002", ReplyMarkup.Text(second.Answer), StringComparison.Ordinal);
        Assert.Equal(2, _db.Constructor.GetRecords(_journal).Count);
    }

    /// <summary>
    /// Пустую серию открывает любой первый документ, а не только 001: опечатка в коде
    /// классификации с явным номером или исполнением открыла бы ее так же молча.
    /// </summary>
    [Theory]
    [InlineData("005", "АБВГ.301121.005")]
    [InlineData("007-ХХ", "АБВГ.301121.007-01")]
    public void Any_first_document_of_an_empty_series_waits_for_confirmation(string tail, string designation)
    {
        var preview = Call(Request(tail));
        Assert.True(preview.Ok, preview.Explanation);
        Assert.Contains($"Номер: {designation}\n", ReplyMarkup.Text(preview.Answer), StringComparison.Ordinal);
        Assert.Contains("Это первый номер", ReplyMarkup.Text(preview.Answer), StringComparison.Ordinal);
        Assert.Empty(_db.Constructor.GetRecords(_journal));

        var done = Call("Да");
        Assert.True(done.Ok, done.Explanation);
        Assert.StartsWith($"Зарегистрировано\nНомер: {designation}\n", ReplyMarkup.Text(done.Answer), StringComparison.Ordinal);
        Assert.Single(_db.Constructor.GetRecords(_journal));
    }

    /// <summary>Ответ — в порядке плана: номер, расшифровка кода по уровням, вопрос и «Да или Нет».</summary>
    [Fact]
    public void The_first_number_question_follows_the_number_and_the_catalog_chain()
    {
        var answer = ReplyMarkup.Text(Call(Request()).Answer);
        var number   = answer.IndexOf("Номер: АБВГ.301121.001", StringComparison.Ordinal);
        var chain    = answer.IndexOf($"{EskdCatalog.Chain("301121").Last().Code} — ", StringComparison.Ordinal);
        var question = answer.IndexOf("Это первый номер в данной серии классификатора. Вы уверены, что номер верный?", StringComparison.Ordinal);
        var prompt   = answer.IndexOf("Подтвердите: Да или Нет", StringComparison.Ordinal);

        Assert.True(number >= 0 && number < chain && chain < question && question < prompt, answer);
    }

    [Theory]
    [InlineData("301121")]
    [InlineData("000000")]
    public void No_cancels_without_writing_and_cannot_be_reused(string code)
    {
        Assert.True(Call(Request(code: code)).Ok);
        Assert.Equal("Операция отменена", ReplyMarkup.Text(Call("Нет").Answer));
        Assert.Empty(_db.Constructor.GetRecords(_journal));
        Assert.False(Call("Да").Ok);
        Assert.True(Call(Request(code: code)).Ok);
        Assert.Empty(_db.Constructor.GetRecords(_journal));
    }

    [Theory]
    [InlineData("002")]
    [InlineData("ХХХ")]
    public void Later_documents_are_registered_without_confirmation(string tail)
    {
        Seed(1);
        var result = Call(Request(tail));
        Assert.True(result.Ok, result.Explanation);
        Assert.StartsWith("Зарегистрировано\nНомер: АБВГ.301121.002", ReplyMarkup.Text(result.Answer), StringComparison.Ordinal);
        Assert.Equal(2, _db.Constructor.GetRecords(_journal).Count);
        Assert.False(Call("Да").Ok);
    }

    [Fact]
    public void A_hole_at_001_in_an_existing_series_needs_no_first_series_warning()
    {
        Seed(2);
        Assert.StartsWith("Зарегистрировано\n", ReplyMarkup.Text(Call(Request()).Answer), StringComparison.Ordinal);
        Assert.Equal(2, _db.Constructor.GetRecords(_journal).Count);
    }

    [Fact]
    public void Another_organization_does_not_make_this_series_existing()
    {
        Seed(1, org: "ГМБВ");
        Assert.Contains("Это первый номер", ReplyMarkup.Text(Call(Request()).Answer), StringComparison.Ordinal);
        Assert.Single(_db.Constructor.GetRecords(_journal));
    }

    [Fact]
    public void Occupied_numbers_and_their_executions_cannot_be_registered_again()
    {
        Seed(1);
        var result = Call(Request());
        Assert.False(result.Ok);
        Assert.Contains("уже зарегистрирован", ReplyMarkup.Text(result.Answer), StringComparison.Ordinal);
        Assert.Single(_db.Constructor.GetRecords(_journal));
    }

    [Fact]
    public void A_changed_series_does_not_silently_change_the_confirmed_number()
    {
        Assert.True(Call(Request("ХХХ")).Ok);
        Seed(1);
        var result = Call("Да");
        Assert.False(result.Ok);
        Assert.Contains("Серия изменилась", ReplyMarkup.Text(result.Answer), StringComparison.Ordinal);
        Assert.Single(_db.Constructor.GetRecords(_journal));
        Assert.False(Call("Да").Ok);
    }

    [Fact]
    public void Expired_confirmation_does_not_write()
    {
        Assert.True(Call(Request()).Ok);
        var result = Call("Да", minutes: 10);
        Assert.False(result.Ok);
        Assert.Contains("срок истек", ReplyMarkup.Text(result.Answer), StringComparison.Ordinal);
        Assert.Empty(_db.Constructor.GetRecords(_journal));
    }

    [Fact]
    public void Confirmation_belongs_to_the_original_sender_and_source()
    {
        Assert.True(Call(Request()).Ok);
        Assert.False(Call("Да", sender: "1002").Ok);
        Assert.False(Call("Да", channel: "other").Ok);
        Assert.Empty(_db.Constructor.GetRecords(_journal));
        Assert.True(Call("Да").Ok);
    }

    [Fact]
    public void A_new_request_cannot_replace_a_pending_registration()
    {
        Assert.True(Call(Request()).Ok);
        Assert.False(Call(Request("002")).Ok);
        Assert.True(Call("Да").Ok);
        Assert.Contains("АБВГ.301121.001", ReplyMarkup.Text(Call("Действие: Просмотр\nНомер: АБВГ.301121.001").Answer), StringComparison.Ordinal);
        Assert.Single(_db.Constructor.GetRecords(_journal));
    }

    [Fact]
    public void Revoked_sender_access_and_org_setting_prevent_writes()
    {
        Assert.True(Call(Request()).Ok);
        Assert.False(Call("Да", senders: "1002").Ok);
        Assert.False(Call("Да", orgs: "ГМБВ").Ok);
        Assert.Empty(_db.Constructor.GetRecords(_journal));
    }

    [Fact]
    public void Revoked_write_permission_is_checked_again_at_confirmation()
    {
        Assert.True(Call(Request()).Ok);
        var userId = _db.Permissions.CurrentUserId;
        _db.Permissions.SetMask(userId, null, (int)PermAction.ViewCollection);
        var me = _db.Context.DbUsers.Single(u => u.Id == userId);
        me.IsAdmin = false;
        _db.Context.SaveChanges();
        _db.Permissions.Reload(notify: false);
        Assert.False(Call("Да").Ok);
        Assert.Empty(_db.Constructor.GetRecords(_journal));
    }

    [Fact]
    public void Invalid_fields_do_not_create_a_confirmation()
    {
        var result = Call(Request(code: "000000").Replace("Наименование: Кронштейн", "Наименование:", StringComparison.Ordinal));
        Assert.False(result.Ok);
        Assert.Contains("Наименование: не указано наименование", ReplyMarkup.Text(result.Answer), StringComparison.Ordinal);
        Assert.False(Call("Да").Ok);
        Assert.Empty(_db.Constructor.GetRecords(_journal));
    }
}
