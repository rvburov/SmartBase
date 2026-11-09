using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Память о повторах входящего канала (<see cref="IngressReplies"/>).
///
/// Отправитель при обрыве связи повторяет обращение — это норма, а не сбой (docs/ROADMAP-API.md,
/// раздел 7). Проверяется главное свойство памяти: она хранит ОТВЕТ, а не отметку «было». Повтор
/// приходит именно затем, чтобы ответ прочитать, и «уже приходило» вместо присвоенного номера
/// для отправителя ничем не лучше молчания.
/// </summary>
public sealed class IngressRepliesTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly string         _path = TempDatabase.NewTempPath();
    private readonly IngressReplies _replies;

    public IngressRepliesTests() => _replies = new IngressReplies(_path);

    public void Dispose() => TempDatabase.Cleanup(_path);

    private static InboxReceipt Receipt(string id, string explanation = "зарегистрировано А-1") =>
        InboxReceipt.Rejected("сеть", id, "Заказы", 1, explanation, Now) with { Accepted = true };

    [Fact]
    public void An_unseen_message_is_not_remembered()
    {
        Assert.Null(_replies.Past(1, "m1"));
    }

    [Fact]
    public void The_remembered_answer_comes_back_whole()
    {
        Assert.True(_replies.Remember(1, "m1", Receipt("m1"), Now));

        var past = _replies.Past(1, "m1");

        Assert.NotNull(past);
        Assert.Equal("m1", past!.Id);
        Assert.Equal("зарегистрировано А-1", past.Explanation);
        Assert.True(past.Accepted);
    }

    /// <summary>
    /// Один и тот же номер сообщения у РАЗНЫХ токенов — это разные отправители, и считать их
    /// повторами друг друга нельзя: первый получил бы чужой ответ, а второй не получил бы записей.
    /// </summary>
    [Fact]
    public void Different_senders_do_not_shadow_each_other()
    {
        _replies.Remember(1, "заказ", Receipt("заказ", "от первого"), Now);

        Assert.Null(_replies.Past(2, "заказ"));
        Assert.Equal("от первого", _replies.Past(1, "заказ")!.Explanation);
    }

    /// <summary>Сообщение без номера повтором не бывает: защищаться нечем.</summary>
    [Fact]
    public void A_message_without_an_id_is_never_a_repeat()
    {
        Assert.True(_replies.Remember(1, "", Receipt(""), Now));

        Assert.Null(_replies.Past(1, ""));
        Assert.Equal(0, _replies.Count());
    }

    [Fact]
    public void Memory_survives_a_reopen()
    {
        _replies.Remember(3, "m7", Receipt("m7"), Now);

        var again = new IngressReplies(_path);

        Assert.NotNull(again.Past(3, "m7"));
    }

    /// <summary>
    /// Помнить вечно незачем: память нужна ровно на то время, пока отправитель может повторить
    /// обращение.
    /// </summary>
    [Fact]
    public void Old_answers_are_forgotten()
    {
        _replies.Remember(1, "старое", Receipt("старое"), Now.AddDays(-IngressReplies.KeepDays - 1));
        _replies.Remember(1, "свежее", Receipt("свежее"), Now);

        Assert.Equal(1, _replies.Forget(Now));

        Assert.Null(_replies.Past(1, "старое"));
        Assert.NotNull(_replies.Past(1, "свежее"));
    }
}
