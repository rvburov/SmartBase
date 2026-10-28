using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBase.Core.Exporters.TableWriters;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>Один сценарий для SQLite и PostgreSQL: вычисляемое значение не становится
/// обычной редактируемой ячейкой, а фиксированный снимок живет до явного пересчета.</summary>
public sealed class DatePeriodPropertyTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Current_time_period_is_available_for_manual_display_but_not_automatic_relation_keys_or_conditions(bool postgres)
    {
        using var f = new Fixture(postgres);
        var source = f.Create("Источник", "2026-10-28T09:00", "2026-10-28T11:00");
        var target = f.Create("Цель", "2026-10-28T09:00", "2026-10-28T11:00");
        var live = f.Repo.AddField(new DbField
        {
            CollectionId = f.Collection, Name = "До текущего времени", FieldType = "date_period", IsSystem = true,
            ConfigJson = new DatePeriodConfig(f.Start).ToConfigJson(),
        }).Id!.Value;
        var fixedNow = f.Repo.AddField(new DbField
        {
            CollectionId = f.Collection, Name = "Фиксированный до момента расчета", FieldType = "date_period", IsSystem = true,
            ConfigJson = new DatePeriodConfig(f.Start, Automatic: false).ToConfigJson(),
        }).Id!.Value;
        var manual = f.Repo.AddField(new DbField
        {
            CollectionId = f.Collection, Name = "Ручная связь", FieldType = "relation",
            ConfigJson = new RelationConfig(f.Collection, false, DisplayField: live.ToString()).ToConfigJson(),
        });
        f.Repo.SetLinks(manual.Id!.Value, source, [target]);
        Assert.Contains(f.Repo.GetRelationPropertyOptions(f.Collection), option => option.Key == live.ToString());
        var offered = f.Repo.GetAutoRelationPropertyOptions(f.Collection);
        Assert.DoesNotContain(offered, option => option.Key == live.ToString());
        Assert.Contains(offered, option => option.Key == f.Automatic.ToString());
        Assert.Contains(offered, option => option.Key == f.Fixed.ToString());
        Assert.Contains(offered, option => option.Key == fixedNow.ToString());

        foreach (var config in new[]
        {
            new RelationConfig(f.Collection, false, Auto: new(live.ToString(), f.Fixed.ToString())),
            new RelationConfig(f.Collection, false, Auto: new(f.Fixed.ToString(), live.ToString())),
            new RelationConfig(f.Collection, false, Auto: new(f.Automatic.ToString(), f.Fixed.ToString()),
                Where: [new RelationCondition(live.ToString(), RelationCondition.Equal, ["7200"])]),
        })
        {
            var proposed = new DbField { Id = manual.Id, CollectionId = f.Collection, Name = manual.Name,
                FieldType = "relation", ConfigJson = config.ToConfigJson() };
            var preview = f.Repo.PreviewAutoRelation(proposed);
            Assert.Equal(RelationAutoStatus.InvalidRule, preview.Status);
            Assert.Contains("текущего времени", preview.Message);
            Assert.False(preview.CanApply);
            Assert.Throws<RelationAutoApplyException>(() => f.Repo.SaveAutoRelationField(proposed, preview));
            Assert.Equal([target], f.Repo.GetLinkTargetIds(manual.Id.Value, source));
            Assert.Null(RelationConfig.FromConfig(f.Repo.GetFields(f.Collection).Single(field => field.Id == manual.Id).ConfigJson)!.Auto);
        }
        var permitted = new DbField { Id = manual.Id, CollectionId = f.Collection, Name = manual.Name, FieldType = "relation",
            ConfigJson = new RelationConfig(f.Collection, false,
                Auto: new(f.Automatic.ToString(), f.Fixed.ToString())).ToConfigJson() };
        Assert.True(f.Repo.PreviewAutoRelation(permitted).CanApply);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lookup_and_rollup_of_a_live_period_cannot_bypass_the_automatic_relation_time_guard(bool postgres)
    {
        using var f = new Fixture(postgres);
        var source = f.Create("Источник", "2026-10-28T09:00", "2026-10-28T11:00");
        var target = f.Create("Цель", "2026-10-28T09:00", "2026-10-28T11:00");
        var live = f.Repo.AddField(new DbField { CollectionId = f.Collection, Name = "Текущий период", FieldType = "date_period",
            ConfigJson = new DatePeriodConfig(f.Start).ToConfigJson(), IsSystem = true }).Id!.Value;
        var data = f.Repo.AddField(new DbField { CollectionId = f.Collection, Name = "Источник значений", FieldType = "relation",
            ConfigJson = new RelationConfig(f.Collection, false).ToConfigJson() }).Id!.Value;
        f.Repo.SetLinks(data, source, [target]);
        var manual = f.Repo.AddField(new DbField { CollectionId = f.Collection, Name = "Проверяемая связь", FieldType = "relation",
            ConfigJson = new RelationConfig(f.Collection, false).ToConfigJson() });
        f.Repo.SetLinks(manual.Id!.Value, source, [target]);
        foreach (var type in new[] { "lookup", "rollup" })
        {
            var computed = f.Repo.AddField(new DbField { CollectionId = f.Collection, Name = "Текущий через " + type,
                FieldType = type, ConfigJson = new RelationComputedConfig(data, live.ToString(),
                    type == "lookup" ? "lookup" : "sum").ToConfigJson() }).Id!.Value;
            Assert.Contains(f.Repo.GetRelationPropertyOptions(f.Collection), option => option.Key == computed.ToString());
            Assert.DoesNotContain(f.Repo.GetAutoRelationPropertyOptions(f.Collection), option => option.Key == computed.ToString());
            var proposed = new DbField { Id = manual.Id, CollectionId = f.Collection, Name = manual.Name, FieldType = "relation",
                ConfigJson = new RelationConfig(f.Collection, false,
                    Auto: new(computed.ToString(), f.Fixed.ToString())).ToConfigJson() };
            var preview = f.Repo.PreviewAutoRelation(proposed);
            Assert.Equal(RelationAutoStatus.InvalidRule, preview.Status);
            Assert.Contains("текущего времени", preview.Message);
            Assert.False(preview.CanApply);
            Assert.Throws<RelationAutoApplyException>(() => f.Repo.SaveAutoRelationField(proposed, preview));
            Assert.Equal([target], f.Repo.GetLinkTargetIds(manual.Id.Value, source));
            Assert.Null(RelationConfig.FromConfig(f.Repo.GetFields(f.Collection).Single(field => field.Id == manual.Id).ConfigJson)!.Auto);
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSQL_recalculation_rereads_metadata_after_a_concurrent_type_change(bool changeSourceType)
    {
        using var f = new Fixture(postgres: true);
        var id = f.Create("Конкурентная правка", "2026-10-28T09:00", "2026-10-28T11:00");
        f.Repo.SetFieldValue(id, f.End, "2026-10-28T13:00");
        Assert.Equal("7200", f.Value(id, f.Fixed));
        var appName = "SmartBase.period-test." + Guid.NewGuid().ToString("N");
        var workerString = new NpgsqlConnectionStringBuilder(PgTestServer.ConnectionString(PgTestServer.Database1))
            { ApplicationName = appName }.ConnectionString;
        using var worker = DatabaseManager.Open(102, "period-race", workerString, provider: "postgres");
        await using var owner = new NpgsqlConnection(PgTestServer.ConnectionString(PgTestServer.Database1));
        await using var observer = new NpgsqlConnection(PgTestServer.ConnectionString(PgTestServer.Database1));
        await owner.OpenAsync();
        await observer.OpenAsync();
        Task<Exception?>? recalculation = null;
        try
        {
            await using (var transaction = await owner.BeginTransactionAsync())
            {
                await using (var change = new NpgsqlCommand("UPDATE db_fields SET fieldtype='text' WHERE id=$1;", owner, transaction))
                {
                    change.Parameters.AddWithValue(changeSourceType ? f.End : f.Fixed);
                    Assert.Equal(1, await change.ExecuteNonQueryAsync());
                }
                recalculation = Task.Run(() => Record.Exception(() => worker.Constructor.RecalculateDatePeriod(id, f.Fixed)));
                var watch = Stopwatch.StartNew();
                while (true)
                {
                    Assert.False(recalculation.IsCompleted, "Пересчет прошел мимо занятой строки метаданных");
                    await using var waiting = new NpgsqlCommand(
                        "SELECT count(*) FROM pg_stat_activity WHERE application_name=$1 AND wait_event_type='Lock';", observer);
                    waiting.Parameters.AddWithValue(appName);
                    if (Equals(1L, await waiting.ExecuteScalarAsync())) break;
                    Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "Пересчет не дошел до блокировки метаданных");
                    await Task.Delay(10);
                }
                // После разблокирования пересчет обязан увидеть новый тип, а не записать
                // расчет по уже устаревшей конфигурации в ставшее текстовым свойство.
                await transaction.CommitAsync();
            }
            var error = await recalculation.WaitAsync(TimeSpan.FromSeconds(15));
            if (changeSourceType) Assert.IsType<InvalidOperationException>(error);
            else Assert.Null(error);
            Assert.Equal("7200", f.Manager.Context.DbFieldValues.AsNoTracking()
                .Single(value => value.RecordId == id && value.FieldId == f.Fixed).Value);
        }
        finally
        {
            // Выход из using откатывает удерживающий lock даже при отказе проверки.
            if (recalculation is not null) await recalculation.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Automatic_values_change_with_source_dates_while_fixed_values_wait_for_recalculation(bool postgres)
    {
        using var f = new Fixture(postgres);
        var id = f.Create("Первая", "2026-10-28T09:00", "2026-10-28T11:00");
        Assert.Equal("7200", f.Value(id, f.Automatic));
        Assert.Equal("7200", f.Value(id, f.Fixed));
        Assert.False(f.Manager.Context.DbFieldValues.Any(v => v.RecordId == id && v.FieldId == f.Automatic));
        Assert.Equal("7200", f.Manager.Context.DbFieldValues.AsNoTracking()
            .Single(v => v.RecordId == id && v.FieldId == f.Fixed).Value);

        f.Repo.SetFieldValue(id, f.End, "2026-10-28T13:00");
        Assert.Equal("14400", f.Value(id, f.Automatic));
        Assert.Equal("7200", f.Value(id, f.Fixed));
        Assert.Equal("14400", f.Repo.GetFieldValuesBatch([id])[id][f.Automatic]);
        var oneColumn = f.Repo.GetFieldValuesBatch([id], [f.Fixed]);
        Assert.Equal("7200", oneColumn[id][f.Fixed]);
        Assert.False(oneColumn[id].ContainsKey(f.Automatic));
        Assert.Equal("14400", f.Repo.RecalculateDatePeriod(id, f.Fixed));
        Assert.Equal("14400", f.Value(id, f.Fixed));
        Assert.Equal("4 ч", FieldTypeInfo.FormatComputedDisplay(f.Value(id, f.Fixed), "date_period"));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Current_time_endpoint_is_live_for_automatic_values_and_frozen_for_fixed_values(bool postgres)
    {
        using var f = new Fixture(postgres);
        int Period(string name, bool automatic) => f.Repo.AddField(new DbField
        {
            CollectionId = f.Collection, Name = name, FieldType = "date_period", IsSystem = true,
            ConfigJson = new DatePeriodConfig(f.Start, Automatic: automatic).ToConfigJson(),
        }).Id!.Value;
        var live = Period("До текущего момента", true);
        var fixedNow = Period("Момент создания", false);
        var start = DateTime.Now.Date.AddDays(-2);
        var startText = start.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
        var before = DateTime.Now;
        var id = f.Create("Сейчас", startText, startText);
        var after = DateTime.Now;
        var frozen = f.Value(id, fixedNow);
        var seconds = long.Parse(frozen, CultureInfo.InvariantCulture);
        Assert.InRange(seconds, (long)(before - start).TotalSeconds, (long)(after - start).TotalSeconds);
        before = DateTime.Now;
        var automaticSeconds = long.Parse(f.Value(id, live), CultureInfo.InvariantCulture);
        after = DateTime.Now;
        static DateTime Minute(DateTime value) => new(value.Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, value.Kind);
        Assert.InRange(automaticSeconds, (long)(Minute(before) - start).TotalSeconds, (long)(Minute(after) - start).TotalSeconds);

        f.Repo.SetFieldValue(id, f.Start, start.AddDays(-1).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture));
        Assert.Equal(frozen, f.Value(id, fixedNow));
        Assert.True(long.Parse(f.Value(id, live), CultureInfo.InvariantCulture) >= automaticSeconds + 86400);
        Assert.False(f.Manager.Context.DbFieldValues.Any(v => v.RecordId == id && v.FieldId == live));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Automatic_current_time_filter_snapshot_expires_at_the_next_minute_without_database_writes(bool postgres)
    {
        using var f = new Fixture(postgres);
        var clock = new DateTime(2026, 10, 28, 9, 0, 1);
        f.Repo.DatePeriodClock = () => clock;
        var live = f.Repo.AddField(new DbField
        {
            CollectionId = f.Collection, Name = "Текущее время в фильтре", FieldType = "date_period", IsSystem = true,
            ConfigJson = new DatePeriodConfig(f.Start).ToConfigJson(),
        }).Id!.Value;
        var first = f.Create("Первая", "2026-10-28T07:00", "2026-10-28T07:00");
        var second = f.Create("Вторая", "2026-10-28T07:00", "2026-10-28T07:00");
        SampleData.AddRecord(f.Repo, f.Collection, "Без даты");
        var key = live.ToString(CultureInfo.InvariantCulture);
        var initial = await f.Repo.GetColumnValueListAsync(f.Collection, key);
        Assert.NotNull(initial.Snapshot);
        Assert.Contains(initial.Values, value => value.Value == "7200" && value.Count == 2);
        Assert.Contains(initial.Values, value => value.Value.Length == 0 && value.Count == 1);

        // Меняются только часы, без правок записей, свойств и настроек коллекции.
        clock = clock.AddSeconds(29);
        var sameMinute = await f.Repo.GetColumnValueListAsync(f.Collection, key, reuse: initial.Snapshot);
        Assert.Same(initial.Snapshot, sameMinute.Snapshot);
        Assert.Equal(initial.Values, sameMinute.Values);

        clock = new DateTime(2026, 10, 28, 9, 0, 45);
        var sameMinuteCounts = await f.Repo.GetColumnCountsAsync(f.Collection, key);
        Assert.Contains(sameMinuteCounts, value => value.Value == "7200" && value.Count == 2);
        var selectedInTheSameMinute = await f.Repo.GetFilteredRecordIdsAsync(f.Collection,
            new Dictionary<string, HashSet<string>> { [key] = ["7200"] }, false, key, true, "date_period", default);
        Assert.Equal([first, second], selectedInTheSameMinute);

        clock = new DateTime(2026, 10, 28, 9, 1, 1);
        var nextMinute = await f.Repo.GetColumnValueListAsync(f.Collection, key, reuse: initial.Snapshot);
        Assert.NotSame(initial.Snapshot, nextMinute.Snapshot);
        Assert.Contains(nextMinute.Values, value => value.Value == "7260" && value.Count == 2);
        Assert.DoesNotContain(nextMinute.Values, value => value.Value == "7200");
        Assert.Contains(nextMinute.Values, value => value.Value.Length == 0 && value.Count == 1);
        var searched = await f.Repo.GetColumnValueListAsync(f.Collection, key, search: "2 ч", reuse: nextMinute.Snapshot);
        Assert.Same(nextMinute.Snapshot, searched.Snapshot);
        Assert.Equal([("7260", 2)], searched.Values);
        var counts = await f.Repo.GetColumnCountsAsync(f.Collection, key);
        Assert.Contains(counts, value => value.Value == "7260" && value.Count == 2);
        var filtered = await f.Repo.GetFilteredRecordIdsAsync(f.Collection,
            new Dictionary<string, HashSet<string>> { [key] = ["7260"] }, false, key, true, "date_period", default);
        Assert.Equal([first, second], filtered);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ordinary_creation_with_no_initial_dates_stays_empty_until_manual_fixed_recalculation(bool postgres)
    {
        using var f = new Fixture(postgres);
        var id = SampleData.AddRecord(f.Repo, f.Collection, "Пустая при создании").Id!.Value;
        Assert.Equal("", f.Value(id, f.Fixed));
        Assert.Equal("", f.Value(id, f.Automatic));
        f.Repo.SetFieldValue(id, f.Start, "2026-10-28");
        Assert.Equal("", f.Value(id, f.Automatic));
        f.Repo.SetFieldValue(id, f.End, "2026-10-29");
        Assert.Equal("86400", f.Value(id, f.Automatic));
        Assert.Equal("", f.Value(id, f.Fixed));
        Assert.Equal("86400", f.Repo.RecalculateDatePeriod(id, f.Fixed));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Initial_values_inside_one_creation_transaction_are_captured_once_and_rollback_leaves_no_snapshot(bool postgres)
    {
        using var f = new Fixture(postgres);
        var count = f.Repo.CountRecords(f.Collection);
        var rollback = new InvalidOperationException("Проверка отката создания");
        var thrown = Assert.Throws<InvalidOperationException>(() => f.Repo.InWriteTransaction<int>(() =>
        {
            var rolledBack = f.Create("Откат", "2026-10-28T09:00", "2026-10-28T11:00");
            Assert.True(rolledBack > 0);
            throw rollback;
        }));
        Assert.Same(rollback, thrown); // Отказ вложенного BeginTransaction не считается проверенным откатом.
        Assert.Equal(count, f.Repo.CountRecords(f.Collection));
        Assert.False(f.Manager.Context.DbFieldValues.AsNoTracking().Any(v => v.FieldId == f.Fixed));

        var id = f.Repo.InWriteTransaction(() =>
        {
            var created = SampleData.AddRecord(f.Repo, f.Collection, "С начальными датами").Id!.Value;
            f.Repo.SetFieldValue(created, f.Start, "2026-10-28T09:00");
            f.Repo.SetFieldValue(created, f.End, "2026-10-28T12:00");
            return created;
        });
        Assert.Equal("10800", f.Value(id, f.Fixed));
        f.Repo.SetFieldValue(id, f.Start, "2026-10-28T10:00");
        Assert.Equal("7200", f.Value(id, f.Automatic));
        Assert.Equal("10800", f.Value(id, f.Fixed));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Implicit_context_transaction_captures_initial_values_once_without_nested_save_changes(bool postgres)
    {
        using var f = new Fixture(postgres);
        var record = new DbRecordEntity
        {
            CollectionId = f.Collection, Title = "Один SaveChanges", Position = 1000,
            CreatedAt = DateTime.Now.ToString("o"),
            FieldValues =
            [
                new DbFieldValueEntity { FieldId = f.Start, Value = "2026-10-28T09:00" },
                new DbFieldValueEntity { FieldId = f.End, Value = "2026-10-28T12:00" },
            ],
        };
        f.Manager.Context.DbRecords.Add(record);
        Assert.Null(f.Manager.Context.Database.CurrentTransaction);
        // Родитель и оба значения пишутся одним SaveChanges и одной неявной EF-транзакцией.
        f.Manager.Context.SaveChanges();
        Assert.True(record.Id > 0);
        Assert.Equal("10800", f.Value(record.Id, f.Fixed));
        Assert.Equal("10800", f.Value(record.Id, f.Automatic));
        Assert.Equal(1, f.Repo.CountRecords(f.Collection));
        Assert.Equal(3, f.Manager.Context.DbFieldValues.AsNoTracking().Count(v => v.RecordId == record.Id));
        Assert.All(f.Manager.Context.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
        // Повторное сохранение не вставляет Added-сущности второй раз и не захватывает снимок заново.
        f.Manager.Context.SaveChanges();
        f.Repo.SetFieldValue(record.Id, f.End, "2026-10-28T14:00");
        Assert.Equal("18000", f.Value(record.Id, f.Automatic));
        Assert.Equal("10800", f.Value(record.Id, f.Fixed));
        Assert.Equal(3, f.Manager.Context.DbFieldValues.AsNoTracking().Count(v => v.RecordId == record.Id));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_caught_nested_savepoint_rollback_does_not_discard_or_leak_creation_snapshots(bool postgres)
    {
        using var f = new Fixture(postgres);
        var rollback = new InvalidOperationException("Откат только вложенного создания");
        var first = 0;
        var last = 0;
        f.Repo.InWriteTransaction(() =>
        {
            int Create(string title, string end)
            {
                var id = SampleData.AddRecord(f.Repo, f.Collection, title).Id!.Value;
                f.Repo.SetFieldValue(id, f.Start, "2026-10-28T09:00");
                f.Repo.SetFieldValue(id, f.End, end);
                return id;
            }
            first = Create("До savepoint", "2026-10-28T11:00");
            var caught = Assert.Throws<InvalidOperationException>(() => f.Repo.InWriteTransaction<int>(() =>
            {
                Create("Откатившаяся", "2026-10-28T18:00");
                throw rollback;
            }));
            Assert.Same(rollback, caught);
            last = Create("После savepoint", "2026-10-28T12:00");
            return 0;
        });
        Assert.Equal(["До savepoint", "После savepoint"], f.Repo.GetRecords(f.Collection).Select(r => r.Title));
        Assert.Equal("7200", f.Value(first, f.Fixed));
        Assert.Equal("10800", f.Value(last, f.Fixed));
        Assert.Equal(2, f.Manager.Context.DbFieldValues.AsNoTracking().Count(v => v.FieldId == f.Fixed));
        Assert.False(f.Manager.Context.DbFieldValues.AsNoTracking().Any(v => v.FieldId == f.Automatic));
        f.Repo.SetFieldValue(first, f.End, "2026-10-28T15:00");
        Assert.Equal("21600", f.Value(first, f.Automatic));
        Assert.Equal("7200", f.Value(first, f.Fixed));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scalar_and_bulk_inputs_cannot_forge_computed_values(bool postgres)
    {
        using var f = new Fixture(postgres);
        var id = f.Create("Первая", "2026-10-28T09:00", "2026-10-28T11:00");
        Assert.Throws<InvalidOperationException>(() => f.Repo.SetFieldValue(id, f.Automatic, "999"));
        Assert.Throws<InvalidOperationException>(() => f.Repo.SetFieldValue(id, f.Fixed, "999"));
        Assert.Equal("7200", f.Value(id, f.Automatic));
        Assert.Equal("7200", f.Value(id, f.Fixed));
        var created = Assert.Single(f.Repo.BulkAddRecords(f.Collection, [f.Start, f.End, f.Automatic, f.Fixed],
            [new BulkRecordRow("Из внешних данных", ["2026-10-28T09:00", "2026-10-28T12:00", "999", "999"])], 1, default));
        Assert.Equal("10800", f.Value(created, f.Automatic));
        Assert.Equal("10800", f.Value(created, f.Fixed));
        Assert.False(f.Manager.Context.DbFieldValues.Any(v => v.FieldId == f.Automatic));
        Assert.DoesNotContain(f.Manager.Context.DbFieldValues.AsNoTracking().Where(v => v.FieldId == f.Fixed), v => v.Value == "999");
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bulk_recalculation_changes_only_selected_rows_and_checks_edit_permission(bool postgres)
    {
        using var f = new Fixture(postgres);
        var first = f.Create("Первая", "2026-10-28T09:00", "2026-10-28T11:00");
        var second = f.Create("Вторая", "2026-10-28T09:00", "2026-10-28T11:00");
        foreach (var id in new[] { first, second }) f.Repo.SetFieldValue(id, f.End, "2026-10-28T13:00");
        f.Repo.RecalculateDatePeriods([first], [f.Fixed]);
        Assert.Equal("14400", f.Value(first, f.Fixed));
        Assert.Equal("7200", f.Value(second, f.Fixed));
        f.Repo.RecalculateDatePeriods([first, second]);
        Assert.Equal("14400", f.Value(second, f.Fixed));

        f.Repo.SetFieldValue(second, f.End, "2026-10-28T14:00");
        var permissions = f.Manager.Permissions;
        permissions.SetMask(permissions.CurrentUserId, null, (int)PermAction.All);
        permissions.SetMask(permissions.CurrentUserId, f.Collection, (int)PermAction.ViewCollection);
        var me = f.Manager.Context.DbUsers.Single(u => u.Id == permissions.CurrentUserId);
        me.IsAdmin = false;
        me.IsApproved = true;
        f.Manager.Context.SaveChanges();
        permissions.Reload(notify: false);
        Assert.Throws<PermissionDeniedException>(() => f.Repo.RecalculateDatePeriod(second, f.Fixed));
        Assert.Throws<PermissionDeniedException>(() => f.Repo.RecalculateDatePeriods([first, second], [f.Fixed]));
        Assert.Equal("14400", f.Value(second, f.Fixed));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Numeric_sort_filter_counts_and_csv_export_agree_with_the_values(bool postgres)
    {
        using var f = new Fixture(postgres);
        var tenHours = f.Create("Десять", "2026-10-28T00:00", "2026-10-28T10:00");
        var twoHours = f.Create("Два", "2026-10-28T00:00", "2026-10-28T02:00");
        var twoAgain = f.Create("Еще два", "2026-10-28T00:00", "2026-10-28T02:00");
        var empty = SampleData.AddRecord(f.Repo, f.Collection, "Без даты").Id!.Value;
        var key = f.Automatic.ToString(CultureInfo.InvariantCulture);
        var sorted = await f.Repo.GetFilteredRecordIdsAsync(f.Collection, new Dictionary<string, HashSet<string>>(), false,
            key, true, "date_period", default);
        Assert.Equal([twoHours, twoAgain, tenHours, empty], sorted);
        var filtered = f.Repo.GetFilteredRecordIds(f.Collection,
            new Dictionary<string, HashSet<string>> { [key] = ["7200"] });
        Assert.Equal([twoHours, twoAgain], filtered);
        var values = await f.Repo.GetColumnValueListAsync(f.Collection, key);
        Assert.Contains(values.Values, v => v.Value == "7200" && v.Count == 2);
        Assert.Contains(values.Values, v => v.Value == "36000" && v.Count == 1);
        Assert.Contains(values.Values, v => v.Value.Length == 0 && v.Count == 1);

        var columns = f.Repo.GetExportColumns(f.Collection, includeHidden: true);
        var index = columns.FindIndex(c => c.FieldId == f.Automatic);
        Assert.True(index >= 0);
        var rows = f.Repo.ReadExportRowsIsolated(f.Collection, columns, false, default).ToList();
        Assert.Equal(["36000", "7200", "7200", ""], rows.Select(row => row[index]));
        using var dir = new TempDir();
        var file = dir.File("periods.csv");
        using (var writer = new CsvTableWriter(file))
        {
            writer.BeginTable("Периоды", columns, writeHeader: true);
            foreach (var row in rows) writer.WriteRow(row);
            writer.EndTable();
            writer.Finish();
        }
        var csv = File.ReadAllText(file);
        Assert.Contains("10 ч", csv);
        Assert.Contains("2 ч", csv);
        Assert.DoesNotContain("36000", csv);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Copying_a_collection_remaps_both_source_dates_and_preserves_the_fixed_snapshot(bool postgres)
    {
        using var f = new Fixture(postgres);
        var source = f.Create("Первая", "2026-10-28T09:00", "2026-10-28T11:00");
        f.Repo.SetFieldValue(source, f.End, "2026-10-28T13:00");
        var collection = f.Repo.DuplicateCollection(f.Collection).Id!.Value;
        var fields = f.Repo.GetFields(collection).ToDictionary(field => field.Name);
        var automatic = fields["Автоматический период"];
        var fixedField = fields["Фиксированный период"];
        foreach (var field in new[] { automatic, fixedField })
        {
            var config = DatePeriodConfig.FromConfig(field.ConfigJson);
            Assert.NotNull(config);
            Assert.Equal(fields["Начало"].Id, config!.StartFieldId);
            Assert.Equal(fields["Конец"].Id, config.EndFieldId);
            Assert.NotEqual(f.Start, config.StartFieldId);
            Assert.NotEqual(f.End, config.EndFieldId);
        }
        var copied = Assert.Single(f.Repo.GetRecords(collection)).Id!.Value;
        Assert.Equal("14400", f.Value(copied, automatic.Id!.Value));
        Assert.Equal("7200", f.Value(copied, fixedField.Id!.Value));
        f.Repo.SetFieldValue(copied, fields["Конец"].Id!.Value, "2026-10-28T14:00");
        Assert.Equal("18000", f.Value(copied, automatic.Id.Value));
        Assert.Equal("7200", f.Value(copied, fixedField.Id.Value));
        Assert.Equal("14400", f.Value(source, f.Automatic));
        Assert.Equal("7200", f.Value(source, f.Fixed));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Configuration_cannot_reference_a_foreign_collection_or_a_non_date_field(bool postgres)
    {
        using var f = new Fixture(postgres);
        var other = SampleData.AddCollection(f.Repo, "Другая").Id!.Value;
        var foreignDate = SampleData.AddField(f.Repo, other, "Чужая дата", "date").Id!.Value;
        var text = SampleData.AddField(f.Repo, f.Collection, "Текст", "text").Id!.Value;
        foreach (var invalid in new[] { foreignDate, text, int.MaxValue })
            Assert.Throws<InvalidOperationException>(() => f.Repo.AddField(new DbField
            {
                CollectionId = f.Collection, Name = "Недопустимый " + invalid, FieldType = "date_period",
                ConfigJson = new DatePeriodConfig(invalid).ToConfigJson(), IsSystem = true,
            }));
        var update = f.Repo.GetFields(f.Collection).Single(field => field.Id == f.Automatic);
        update.ConfigJson = new DatePeriodConfig(f.Start, foreignDate).ToConfigJson();
        Assert.Throws<InvalidOperationException>(() => f.Repo.UpdateField(update));
        var restored = DatePeriodConfig.FromConfig(f.Repo.GetFields(f.Collection).Single(field => field.Id == f.Automatic).ConfigJson);
        Assert.Equal(f.End, restored!.EndFieldId);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDatabase? _sqlite;
        public DatabaseManager Manager { get; }
        public ConstructorRepository Repo => Manager.Constructor;
        public int Collection { get; }
        public int Start { get; }
        public int End { get; }
        public int Automatic { get; }
        public int Fixed { get; }

        public Fixture(bool postgres)
        {
            if (!postgres)
            {
                _sqlite = new TempDatabase(101, "date-period");
                Manager = _sqlite.Manager;
            }
            else
            {
                Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
                var connectionString = PgTestServer.ConnectionString(PgTestServer.Database1);
                using (var connection = new NpgsqlConnection(connectionString))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
                    command.ExecuteNonQuery();
                }
                Manager = DatabaseManager.Open(101, "date-period", connectionString, provider: "postgres");
            }
            Collection = SampleData.AddCollection(Repo, "Периоды").Id!.Value;
            Start = SampleData.AddField(Repo, Collection, "Начало", "date").Id!.Value;
            End = SampleData.AddField(Repo, Collection, "Конец", "date").Id!.Value;
            Automatic = Period("Автоматический период", automatic: true);
            Fixed = Period("Фиксированный период", automatic: false);
        }

        private int Period(string name, bool automatic) => Repo.AddField(new DbField
        {
            CollectionId = Collection, Name = name, FieldType = "date_period", IsSystem = true,
            ConfigJson = new DatePeriodConfig(Start, End, Automatic: automatic).ToConfigJson(),
        }).Id!.Value;

        public int Create(string title, string start, string end) => Assert.Single(Repo.BulkAddRecords(Collection, [Start, End],
            [new BulkRecordRow(title, [start, end])], 1, default));

        public string Value(int record, int field) => Repo.GetFieldValues(record).GetValueOrDefault(field, "");

        public void Dispose()
        {
            if (_sqlite is not null) _sqlite.Dispose();
            else Manager.Dispose();
        }
    }
}
