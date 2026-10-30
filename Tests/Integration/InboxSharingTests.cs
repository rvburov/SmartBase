using SmartBase.Core.Importers.TableSources;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>Проверка брошенных захватов не мешает владельцу читать свежий файл в несколько проходов.</summary>
[Collection(EgressCollection.Name)]
public sealed class InboxSharingTests
{
    [Fact]
    public async Task A_fresh_claim_can_be_streamed_while_other_workers_check_for_stale_claims()
    {
        var root = Directory.CreateTempSubdirectory("sb_inbox_sharing_").FullName;
        try
        {
            var now = new DateTimeOffset(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);
            var layout = InboxFolder.Open(root, out var problem);
            Assert.NotNull(layout);
            Assert.Equal("", problem);
            var path = Path.Combine(layout.Incoming, "данные.json");
            File.WriteAllText(path, """
                {"collection":"Заказы","mode":"add","id":"sharing-regression",
                 "rows":[{"Название":"Единственная запись","Ключ":"1"}]}
                """);
            File.SetLastWriteTimeUtc(path, now.AddMinutes(-1).UtcDateTime);
            var claimed = InboxFolder.TryClaim(new InboxFile(path, "данные.json"), "Владелец", now);
            Assert.NotNull(claimed);

            var ownershipOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var cleanup = Task.Run(() => InboxFolder.ReclaimStale(layout, now, _ =>
            {
                ownershipOpened.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Читатель не освободил проверочный захват");
            }));
            try
            {
                // Прежняя реализация захватывала свежий файл и держала FileShare.None.
                // Исправленная завершает уборку, не открывая этот inode монопольно.
                await Task.WhenAny(ownershipOpened.Task, cleanup).WaitAsync(TimeSpan.FromSeconds(5));
                var source = new JsonTableSource(claimed, "rows");
                var schema = source.Analyze(null, default);
                var row = Assert.Single(source.ReadRows(default));
                Assert.Equal(1, schema.RowCount);
                Assert.Equal("Единственная запись", row.Title);
                Assert.Equal(new[] { "1" }, row.FieldValues);
            }
            finally
            {
                release.Set();
                Assert.Equal(0, await cleanup.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            Assert.True(File.Exists(claimed));
            Assert.False(File.Exists(path));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
