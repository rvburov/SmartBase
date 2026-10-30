using SmartBase.Extensions;
using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>Произвольная рассылка: только выбранным, один запрос на участника, явный итог.</summary>
public sealed class ParticipantBroadcastTests
{
    private const string Participants = "1001 Иванов; 1002 Петров; 1003 Сидоров";

    [Fact]
    public void Participants_keep_original_order_labels_and_first_duplicate_spelling()
    {
        var recipients = ParticipantBroadcast.Participants(" USER1   Иванов ; user2 Петров, user1 Повтор\n1003");

        Assert.Equal(new[] { new BroadcastRecipient("USER1", "USER1 Иванов"),
            new BroadcastRecipient("user2", "user2 Петров"), new BroadcastRecipient("1003", "1003") }, recipients);
    }

    [Fact]
    public void Selection_uses_current_participants_and_ignores_stale_ids()
    {
        var selected = ParticipantBroadcast.Select("1001 Иванов; USER2 Петров; 1003 Сидоров",
            [" user2 ", "1001", "1001", "removed", ""]);

        Assert.Equal(new[] { new BroadcastRecipient("1001", "1001 Иванов"),
            new BroadcastRecipient("USER2", "USER2 Петров") }, selected);
        Assert.Empty(ParticipantBroadcast.Select("", ["1001"]));
        Assert.Empty(ParticipantBroadcast.Select(Participants, []));
    }

    [Fact]
    public void Selecting_every_current_id_includes_every_participant_once()
    {
        var all = ParticipantBroadcast.Participants(Participants);
        Assert.Equal(all, ParticipantBroadcast.Select(Participants, all.Select(p => p.Id).ToArray()));
    }

    [Fact]
    public async Task Only_the_selected_recipients_receive_the_literal_text_through_the_registration_route()
    {
        var replies = new FakeReplies();
        var targets = ParticipantBroadcast.Select(Participants, ["1003", "1001"]);
        var progress = new List<BroadcastProgress>();

        var report = await ParticipantBroadcast.SendAsync(replies, targets, "Текст <b>буквами</b> & данные\r\nСтрока 2",
            new SyncProgress(progress.Add));

        Assert.Equal(2, report.Sent);
        Assert.Equal(2, report.Total);
        Assert.Equal(2, report.Attempted);
        Assert.Equal(0, report.NotAttempted);
        Assert.Empty(report.Failed);
        Assert.False(report.Cancelled);
        Assert.Equal("", report.Problem);
        Assert.Null(report.Interrupted);
        Assert.Equal(["1001", "1003"], replies.Calls.Select(c => c.Recipient));
        Assert.All(replies.Calls, c =>
        {
            Assert.Equal(DocRegistryExtension.ExtensionId, c.Extension);
            Assert.Equal(RegisterMessageAction.ActionKey, c.Action);
            Assert.Equal(RegisterMessageAction.SenderField, c.Field);
            Assert.Equal("Текст &lt;b&gt;буквами&lt;/b&gt; &amp; данные\nСтрока 2", c.Text);
        });
        var route = Assert.Single(replies.Routes);
        Assert.Equal((DocRegistryExtension.ExtensionId, RegisterMessageAction.ActionKey,
            RegisterMessageAction.SenderField), route);
        Assert.Equal(0, progress[0].Completed);
        Assert.Equal(2, progress[^1].Completed);
        Assert.All(progress, p => Assert.Equal(2, p.Total));
    }

    [Fact]
    public async Task Duplicate_addresses_are_sent_once_even_without_the_selection_helper()
    {
        var replies = new FakeReplies();
        var report = await ParticipantBroadcast.SendAsync(replies,
            [new(" User1 ", "Иванов"), new("user1", "Другой текст"), new("", "Пустой")], "Сообщение");

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Sent);
        Assert.Equal("User1", Assert.Single(replies.Calls).Recipient);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t ")]
    public async Task An_empty_message_does_not_start_any_delivery(string? text)
    {
        var replies = new FakeReplies();
        var report = await ParticipantBroadcast.SendAsync(replies, ParticipantBroadcast.Participants(Participants), text);

        Assert.Equal("Не указан текст сообщения.", report.Problem);
        Assert.Equal(0, report.Sent);
        Assert.Equal(3, report.NotAttempted);
        Assert.Empty(report.Failed);
        Assert.Empty(replies.Calls);
        Assert.Empty(replies.Routes);
    }

    [Fact]
    public async Task An_empty_selection_does_not_start_any_delivery()
    {
        var replies = new FakeReplies();
        var report = await ParticipantBroadcast.SendAsync(replies, [], "Сообщение");

        Assert.Equal("Не выбраны получатели сообщения.", report.Problem);
        Assert.Equal(0, report.Total);
        Assert.Empty(replies.Calls);
        Assert.Empty(replies.Routes);
    }

    [Fact]
    public async Task Missing_integrations_leave_all_participants_not_attempted()
    {
        var report = await ParticipantBroadcast.SendAsync(null, ParticipantBroadcast.Participants(Participants), "Сообщение");

        Assert.Contains("интеграции недоступны", report.Problem, StringComparison.Ordinal);
        Assert.Equal(0, report.Attempted);
        Assert.Equal(3, report.NotAttempted);
        Assert.Empty(report.Failed);
    }

    [Fact]
    public async Task A_missing_route_blocks_the_whole_send_before_the_first_recipient()
    {
        var replies = new FakeReplies { Scenario = null, RouteProblem = "найдены два сценария" };
        var report = await ParticipantBroadcast.SendAsync(replies, ParticipantBroadcast.Participants(Participants), "Сообщение");

        Assert.Contains("найдены два сценария", report.Problem, StringComparison.Ordinal);
        Assert.Equal(0, report.Attempted);
        Assert.Equal(3, report.NotAttempted);
        Assert.Empty(report.Failed);
        Assert.Empty(replies.Calls);
    }

    [Fact]
    public async Task A_route_lookup_exception_is_a_preflight_failure()
    {
        var replies = new FakeReplies { RouteFailure = new InvalidOperationException("путь недоступен") };
        var report = await ParticipantBroadcast.SendAsync(replies, ParticipantBroadcast.Participants(Participants), "Сообщение");

        Assert.Contains("путь недоступен", report.Problem, StringComparison.Ordinal);
        Assert.Equal(3, report.NotAttempted);
        Assert.Empty(replies.Calls);
    }

    [Fact]
    public async Task One_refusal_does_not_stop_later_recipients_and_is_not_retried()
    {
        var replies = new FakeReplies
        {
            Send = (call, _) => Task.FromResult(new ReplyResult(call.Recipient != "1002", "ошибка доставки")),
        };

        var report = await ParticipantBroadcast.SendAsync(replies, ParticipantBroadcast.Participants(Participants), "Сообщение");

        Assert.Equal(2, report.Sent);
        Assert.Equal(3, report.Attempted);
        Assert.Equal(0, report.NotAttempted);
        Assert.False(report.Cancelled);
        var failure = Assert.Single(report.Failed);
        Assert.Equal(new BroadcastRecipient("1002", "1002 Петров"), failure.Recipient);
        Assert.Equal("ошибка доставки", failure.Problem);
        Assert.Equal(["1001", "1002", "1003"], replies.Calls.Select(c => c.Recipient));
    }

    [Fact]
    public async Task A_transport_exception_is_reported_for_its_recipient_and_others_continue()
    {
        var replies = new FakeReplies
        {
            Send = (call, _) => call.Recipient == "1002"
                ? Task.FromException<ReplyResult>(new InvalidOperationException("контрольная ошибка"))
                : Task.FromResult(new ReplyResult(true, "")),
        };

        var report = await ParticipantBroadcast.SendAsync(replies, ParticipantBroadcast.Participants(Participants), "Сообщение");

        Assert.Equal(2, report.Sent);
        Assert.Equal("1002", Assert.Single(report.Failed).Recipient.Id);
        Assert.Equal("контрольная ошибка", report.Failed[0].Problem);
        Assert.Equal(3, replies.Calls.Count);
    }

    [Fact]
    public async Task A_cancelled_transport_without_our_cancellation_is_a_failure_not_a_global_stop()
    {
        var replies = new FakeReplies
        {
            Send = (call, _) => call.Recipient == "1002"
                ? Task.FromException<ReplyResult>(new OperationCanceledException("истекло время запроса"))
                : Task.FromResult(new ReplyResult(true, "")),
        };

        var report = await ParticipantBroadcast.SendAsync(replies, ParticipantBroadcast.Participants(Participants), "Сообщение");

        Assert.Equal(2, report.Sent);
        Assert.Single(report.Failed);
        Assert.False(report.Cancelled);
        Assert.Null(report.Interrupted);
        Assert.Equal(3, replies.Calls.Count);
    }

    [Fact]
    public async Task Cancellation_before_start_does_not_even_query_a_route()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var replies = new FakeReplies();
        var report = await ParticipantBroadcast.SendAsync(replies, ParticipantBroadcast.Participants(Participants), "Сообщение", ct: stop.Token);

        Assert.True(report.Cancelled);
        Assert.Equal(0, report.Attempted);
        Assert.Equal(3, report.NotAttempted);
        Assert.Null(report.Interrupted);
        Assert.Empty(replies.Calls);
        Assert.Empty(replies.Routes);
    }

    [Fact]
    public async Task Cancellation_between_recipients_keeps_successes_and_the_not_attempted_remainder()
    {
        using var stop = new CancellationTokenSource();
        var replies = new FakeReplies
        {
            Send = (_, _) => { stop.Cancel(); return Task.FromResult(new ReplyResult(true, "")); },
        };

        var report = await ParticipantBroadcast.SendAsync(replies, ParticipantBroadcast.Participants(Participants), "Сообщение", ct: stop.Token);

        Assert.True(report.Cancelled);
        Assert.Equal(1, report.Sent);
        Assert.Equal(1, report.Attempted);
        Assert.Equal(2, report.NotAttempted);
        Assert.Empty(report.Failed);
        Assert.Null(report.Interrupted);
        Assert.Single(replies.Calls);
    }

    [Fact]
    public async Task Cancellation_of_an_inflight_request_does_not_claim_a_delivery_or_a_known_failure()
    {
        using var stop = new CancellationTokenSource();
        var replies = new FakeReplies
        {
            Send = (_, token) => { stop.Cancel(); return Task.FromCanceled<ReplyResult>(token); },
        };

        var report = await ParticipantBroadcast.SendAsync(replies, ParticipantBroadcast.Participants(Participants), "Сообщение", ct: stop.Token);

        Assert.True(report.Cancelled);
        Assert.Equal(0, report.Sent);
        Assert.Empty(report.Failed);
        Assert.Equal(new BroadcastRecipient("1001", "1001 Иванов"), report.Interrupted);
        Assert.Equal(1, report.Attempted);
        Assert.Equal(2, report.NotAttempted);
        Assert.Single(replies.Calls);
    }

    [Fact]
    public async Task Recipient_snapshot_does_not_change_during_the_await()
    {
        var first = new TaskCompletionSource<ReplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replies = new FakeReplies
        {
            Send = (call, _) => call.Recipient == "1001" ? first.Task : Task.FromResult(new ReplyResult(true, "")),
        };
        var recipients = ParticipantBroadcast.Participants(Participants).ToArray();

        var sending = ParticipantBroadcast.SendAsync(replies, recipients, "Сообщение");
        recipients[1] = new BroadcastRecipient("другой", "Уже другая настройка");
        first.SetResult(new ReplyResult(true, ""));
        var report = await sending;

        Assert.Equal(3, report.Sent);
        Assert.Equal(["1001", "1002", "1003"], replies.Calls.Select(c => c.Recipient));
    }

    private sealed record Call(string Extension, string Action, string Field, string Recipient, string Text);

    private sealed class FakeReplies : IActionReplies
    {
        public string? Scenario { get; init; } = "Сценарий сообщений";
        public string RouteProblem { get; init; } = "";
        public Exception? RouteFailure { get; init; }
        public Func<Call, CancellationToken, Task<ReplyResult>>? Send { get; init; }
        public List<(string Extension, string Action, string Field)> Routes { get; } = [];
        public List<Call> Calls { get; } = [];

        public string? Route(string extensionId, IExtensionAction action, string senderField, out string problem)
        {
            Routes.Add((extensionId, action.Key, senderField));
            if (RouteFailure is { } failure) throw failure;
            problem = RouteProblem;
            return Scenario;
        }

        public Task<ReplyResult> SendAsync(string extensionId, IExtensionAction action, string senderField,
            string recipient, string text, CancellationToken ct = default)
        {
            var call = new Call(extensionId, action.Key, senderField, recipient, text);
            Calls.Add(call);
            return Send?.Invoke(call, ct) ?? Task.FromResult(new ReplyResult(true, ""));
        }
    }

    private sealed class SyncProgress(Action<BroadcastProgress> receive) : IProgress<BroadcastProgress>
    {
        public void Report(BroadcastProgress value) => receive(value);
    }
}
