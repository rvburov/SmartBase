using System.Collections.Concurrent;
using SmartBase.Database;

namespace SmartBase.Tests.Unit;

public sealed class WriteAdmissionTests
{
    [Fact]
    public async Task Cancelling_an_async_waiter_withdraws_it_without_consuming_the_next_slot()
    {
        var admission = new WriteAdmission(1);
        using var holder = admission.Enter(TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        var pending = admission.EnterAsync(TimeSpan.FromSeconds(2), cancellation.Token).AsTask();
        Assert.Equal(1, admission.WaitingCount);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        Assert.Equal(0, admission.WaitingCount);
        holder.Dispose();
        using var next = await admission.EnterAsync(TimeSpan.Zero);
        Assert.Throws<TimeoutException>(() => admission.Enter(TimeSpan.Zero));
    }

    [Fact]
    public async Task Cancellation_racing_handoff_cannot_leak_or_duplicate_a_slot()
    {
        var admission = new WriteAdmission(1);
        for (var round = 0; round < 32; round++)
        {
            using var holder = admission.Enter(TimeSpan.Zero);
            using var cancellation = new CancellationTokenSource();
            var pending = admission.EnterAsync(TimeSpan.FromSeconds(2), cancellation.Token).AsTask();
            var cancel = Task.Run(() => cancellation.Cancel());
            holder.Dispose();
            try { (await pending).Dispose(); }
            catch (OperationCanceledException) { }
            await cancel;
            Assert.Equal(0, admission.WaitingCount);
            using var next = admission.Enter(TimeSpan.Zero);
            Assert.Throws<TimeoutException>(() => admission.Enter(TimeSpan.Zero));
        }
    }

    [Fact]
    public void An_earlier_waiter_owns_the_released_slot_before_a_new_caller_can_take_it()
    {
        var admission = new WriteAdmission(1);
        using var holder = admission.Enter(TimeSpan.Zero);
        using var acquired = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var failures = new ConcurrentQueue<Exception>();
        var waiter = new Thread(() =>
        {
            try
            {
                using var lease = admission.Enter(TimeSpan.FromSeconds(3));
                acquired.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(3)));
            }
            catch (Exception error) { failures.Enqueue(error); }
        }) { IsBackground = true };
        waiter.Start();
        try
        {
            Assert.True(SpinWait.SpinUntil(() => admission.WaitingCount == 1, TimeSpan.FromSeconds(2)));
            holder.Dispose();
            // Даже если старший поток еще не получил процессор, слот уже принадлежит ему.
            Assert.Throws<TimeoutException>(() => admission.Enter(TimeSpan.Zero));
            Assert.True(acquired.Wait(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            release.Set();
            Assert.True(waiter.Join(TimeSpan.FromSeconds(4)));
        }
        Assert.Empty(failures);
        using var next = admission.Enter(TimeSpan.Zero);
    }

    [Fact]
    public void A_timed_out_waiter_is_removed_and_disposing_a_lease_twice_cannot_create_a_slot()
    {
        var admission = new WriteAdmission(1);
        var holder = admission.Enter(TimeSpan.Zero);
        var failure = Record.Exception(() => admission.Enter(TimeSpan.FromMilliseconds(20)));
        Assert.IsType<TimeoutException>(failure);
        Assert.Equal(0, admission.WaitingCount);
        holder.Dispose();
        holder.Dispose();
        using (admission.Enter(TimeSpan.Zero))
            Assert.Throws<TimeoutException>(() => admission.Enter(TimeSpan.Zero));
        using var final = admission.Enter(TimeSpan.Zero);
    }

    [Fact]
    public void Waiters_are_granted_in_arrival_order_across_several_handoffs()
    {
        var admission = new WriteAdmission(1);
        using var holder = admission.Enter(TimeSpan.Zero);
        var order = new ConcurrentQueue<int>();
        var failures = new ConcurrentQueue<Exception>();
        var threads = new List<Thread>();
        try
        {
            for (var index = 0; index < 8; index++)
            {
                var id = index;
                var thread = new Thread(() =>
                {
                    try
                    {
                        using var lease = admission.Enter(TimeSpan.FromSeconds(5));
                        order.Enqueue(id);
                    }
                    catch (Exception error) { failures.Enqueue(error); }
                }) { IsBackground = true };
                threads.Add(thread);
                thread.Start();
                Assert.True(SpinWait.SpinUntil(() => admission.WaitingCount == id + 1, TimeSpan.FromSeconds(2)));
            }
        }
        finally
        {
            holder.Dispose();
            var joined = true;
            foreach (var thread in threads) joined &= thread.Join(TimeSpan.FromSeconds(6));
            Assert.True(joined);
        }
        Assert.Empty(failures);
        Assert.Equal(Enumerable.Range(0, 8), order);
    }
}
