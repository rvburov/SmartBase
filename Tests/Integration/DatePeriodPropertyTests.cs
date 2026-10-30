using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
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
    public void Automatic_range_period_is_available_as_a_relation_key(bool postgres)
    {
        using var f = new Fixture(postgres);
        var source = f.Create("Источник", "2026-10-28T09:00", "2026-10-28T11:00");
        var targetCollection = SampleData.AddCollection(f.Repo, "Цели по длительности").Id!.Value;
        var dates = SampleData.AddField(f.Repo, targetCollection, "Диапазон", "date").Id!.Value;
        var period = f.Repo.AddField(new DbField
        {
            CollectionId = targetCollection, Name = "Период цели", FieldType = "date_period", IsSystem = true,
            ConfigJson = new DatePeriodConfig(dates).ToConfigJson(),
        }).Id!.Value;
        var targets = f.Repo.BulkAddRecords(targetCollection, [dates],
        [
            new BulkRecordRow("Два часа", ["2026-10-28T10:00/2026-10-28T12:00"]),
            new BulkRecordRow("Три часа", ["2026-10-28T10:00/2026-10-28T13:00"]),
        ], 2, default);
        var relation = f.Repo.AddField(new DbField
        {
            CollectionId = f.Collection, Name = "По периоду", FieldType = "relation",
            ConfigJson = new RelationConfig(targetCollection, false, DisplayField: period.ToString()).ToConfigJson(),
        });
        Assert.Contains(f.Repo.GetRelationPropertyOptions(f.Collection), option => option.Key == f.Automatic.ToString());
        Assert.Contains(f.Repo.GetAutoRelationPropertyOptions(f.Collection), option => option.Key == f.Automatic.ToString());
        Assert.Contains(f.Repo.GetAutoRelationPropertyOptions(targetCollection), option => option.Key == period.ToString());
        relation.ConfigJson = new RelationConfig(targetCollection, false,
            Auto: new(f.Automatic.ToString(), period.ToString())).ToConfigJson();
        var preview = f.Repo.PreviewAutoRelation(relation);
        Assert.True(preview.CanApply, preview.Message);
        f.Repo.SaveAutoRelationField(relation, preview);
        Assert.Equal([targets[0]], f.Repo.GetLinkTargetIds(relation.Id!.Value, source));
        Assert.NotNull(RelationConfig.FromConfig(f.Repo.GetFields(f.Collection).Single(field => field.Id == relation.Id).ConfigJson)!.Auto);
    }

    [SkippableTheory]
    [InlineData(false, "source")]
    [InlineData(true, "source")]
    [InlineData(false, "target")]
    [InlineData(true, "target")]
    [InlineData(false, "fixed")]
    [InlineData(true, "fixed")]
    public void Source_date_changes_refresh_automatic_relation_dependencies_but_leave_fixed_periods_untouched(bool postgres, string usage)
    {
        using var f = new Fixture(postgres);
        var source = f.Create("Общее название", "2026-10-28T09:00", "2026-10-28T11:00");
        var targetCollection = SampleData.AddCollection(f.Repo, "Цели после правки диапазона").Id!.Value;
        var dates = SampleData.AddField(f.Repo, targetCollection, "Диапазон", "date").Id!.Value;
        int Period(bool automatic) => f.Repo.AddField(new DbField
        {
            CollectionId = targetCollection, Name = automatic ? "Расчетный период" : "Снимок периода",
            FieldType = "date_period", IsSystem = true,
            ConfigJson = new DatePeriodConfig(dates, Automatic: automatic).ToConfigJson(),
        }).Id!.Value;
        var automaticTarget = Period(true);
        var fixedTarget = Period(false);
        var targets = f.Repo.BulkAddRecords(targetCollection, [dates],
        [
            new BulkRecordRow("Общее название", ["2026-10-28T09:00/2026-10-28T11:00"]),
            new BulkRecordRow("Общее название", ["2026-10-28T09:00/2026-10-28T12:00"]),
        ], 2, default);
        var sourceKey = usage == "source" ? f.Automatic.ToString() : f.Fixed.ToString();
        var targetKey = usage == "target" ? automaticTarget.ToString() : fixedTarget.ToString();
        var relation = f.Repo.AddField(new DbField
        {
            CollectionId = f.Collection, Name = "Зависимость от исходного диапазона", FieldType = "relation",
            ConfigJson = new RelationConfig(targetCollection, false).ToConfigJson(),
        });
        relation.ConfigJson = new RelationConfig(targetCollection, false,
            Auto: new(sourceKey, targetKey)).ToConfigJson();
        var preview = f.Repo.PreviewAutoRelation(relation);
        Assert.True(preview.CanApply, preview.Message);
        f.Repo.SaveAutoRelationField(relation, preview);
        Assert.Equal([targets[0]], f.Repo.GetLinkTargetIds(relation.Id!.Value, source));

        // Прямой SQL намеренно обходит SetFieldValue и его уведомление о расчетной колонке:
        // обновление должно найти зависимость именно от исходного свойства даты.
        var context = f.Manager.Context;
        if (usage is "source" or "fixed")
        {
            context.Database.ExecuteSqlInterpolated($@"UPDATE db_field_values
                SET value='2026-10-28T09:00/2026-10-28T12:00'
                WHERE recordid={source} AND fieldid={f.Start}");
        }
        else
        {
            context.Database.ExecuteSqlInterpolated($@"UPDATE db_field_values
                SET value='2026-10-28T09:00/2026-10-28T13:00'
                WHERE recordid={targets[0]} AND fieldid={dates}");
            context.Database.ExecuteSqlInterpolated($@"UPDATE db_field_values
                SET value='2026-10-28T09:00/2026-10-28T11:00'
                WHERE recordid={targets[1]} AND fieldid={dates}");
        }
        context.ChangeTracker.Clear();
        var refreshed = usage is "source" or "fixed"
            ? f.Repo.RefreshAutoRelationsForChange(f.Collection, f.Start)
            : f.Repo.RefreshAutoRelationsForChange(targetCollection, dates);
        if (usage == "fixed")
        {
            Assert.Empty(refreshed);
            Assert.Equal("7200", f.Value(source, f.Fixed));
            Assert.Equal([targets[0]], f.Repo.GetLinkTargetIds(relation.Id.Value, source));
        }
        else
        {
            var result = Assert.Single(refreshed);
            Assert.True(result.Applied, result.Message);
            Assert.True(result.Changed > 0);
            Assert.Equal([targets[1]], f.Repo.GetLinkTargetIds(relation.Id.Value, source));
        }
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Missing_or_retyped_date_source_keeps_existing_auto_links_and_fixed_historical_keys(bool postgres, bool deleteSource)
    {
        using var f = new Fixture(postgres);
        var source = f.Create("Источник со снимком", "2026-10-28T09:00", "2026-10-28T11:00");
        var targetCollection = SampleData.AddCollection(f.Repo, "Цели исправного диапазона").Id!.Value;
        var dates = SampleData.AddField(f.Repo, targetCollection, "Диапазон", "date").Id!.Value;
        var period = f.Repo.AddField(new DbField
        {
            CollectionId = targetCollection, Name = "Период цели", FieldType = "date_period", IsSystem = true,
            ConfigJson = new DatePeriodConfig(dates).ToConfigJson(),
        }).Id!.Value;
        var target = Assert.Single(f.Repo.BulkAddRecords(targetCollection, [dates],
            [new BulkRecordRow("Два часа", ["2026-10-28T09:00/2026-10-28T11:00"])], 1, default));
        DbField Relation(int key, string name)
        {
            var field = f.Repo.AddField(new DbField
            {
                CollectionId = f.Collection, Name = name, FieldType = "relation",
                ConfigJson = new RelationConfig(targetCollection, false).ToConfigJson(),
            });
            field.ConfigJson = new RelationConfig(targetCollection, false,
                Auto: new(key.ToString(), period.ToString())).ToConfigJson();
            var preview = f.Repo.PreviewAutoRelation(field);
            Assert.True(preview.CanApply, preview.Message);
            f.Repo.SaveAutoRelationField(field, preview);
            Assert.Equal([target], f.Repo.GetLinkTargetIds(field.Id!.Value, source));
            return field;
        }
        var automaticRelation = Relation(f.Automatic, "По расчетному периоду");
        var fixedRelation = Relation(f.Fixed, "По сохраненному периоду");

        if (deleteSource) f.Repo.DeleteField(f.Start);
        else
        {
            var changed = f.Repo.GetFields(f.Collection).Single(field => field.Id == f.Start);
            changed.FieldType = "text";
            f.Repo.UpdateField(changed);
        }
        // JSON самого периода остается читаемым: повреждена ссылка на свойство даты,
        // а не формат настройки. Такой пустой результат не должен удалить старые связи.
        var config = DatePeriodConfig.FromConfig(f.Repo.GetFields(f.Collection)
            .Single(field => field.Id == f.Automatic).ConfigJson);
        Assert.NotNull(config);
        Assert.Equal(f.Start, config!.DateFieldId);
        var offered = f.Repo.GetAutoRelationPropertyOptions(f.Collection);
        Assert.DoesNotContain(offered, option => option.Key == f.Automatic.ToString());
        Assert.Contains(offered, option => option.Key == f.Fixed.ToString());
        var rejected = f.Repo.PreviewAutoRelation(automaticRelation);
        Assert.Equal(RelationAutoStatus.InvalidRule, rejected.Status);
        Assert.False(rejected.CanApply);
        Assert.Contains("удалено или изменило тип", rejected.Message, StringComparison.Ordinal);
        Assert.Contains("прежние связи сохранены", rejected.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<RelationAutoApplyException>(() => f.Repo.SaveAutoRelationField(automaticRelation, rejected));
        var refreshed = Assert.Single(f.Repo.RefreshAutoRelationsForChange(f.Collection, f.Start));
        Assert.Equal(RelationAutoStatus.InvalidRule, refreshed.Status);
        Assert.False(refreshed.Applied);
        Assert.Equal(0, refreshed.Changed);
        Assert.Equal([target], f.Repo.GetLinkTargetIds(automaticRelation.Id!.Value, source));

        Assert.Equal("7200", f.Value(source, f.Fixed));
        var historical = f.Repo.PreviewAutoRelation(fixedRelation);
        Assert.True(historical.CanApply, historical.Message);
        f.Repo.SaveAutoRelationField(fixedRelation, historical);
        Assert.Equal([target], f.Repo.GetLinkTargetIds(fixedRelation.Id!.Value, source));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lookup_and_rollup_of_a_range_period_are_offered_for_automatic_relation_keys(bool postgres)
    {
        using var f = new Fixture(postgres);
        var source = f.Create("Источник", "2026-10-28T09:00", "2026-10-28T11:00");
        var target = f.Create("Цель", "2026-10-28T09:00", "2026-10-28T11:00");
        var data = f.Repo.AddField(new DbField { CollectionId = f.Collection, Name = "Источник значений", FieldType = "relation",
            ConfigJson = new RelationConfig(f.Collection, false).ToConfigJson() }).Id!.Value;
        f.Repo.SetLinks(data, source, [target]);
        foreach (var type in new[] { "lookup", "rollup" })
        {
            var computed = f.Repo.AddField(new DbField { CollectionId = f.Collection, Name = "Период через " + type,
                FieldType = type, ConfigJson = new RelationComputedConfig(data, f.Automatic.ToString(),
                    type == "lookup" ? "lookup" : "sum").ToConfigJson() }).Id!.Value;
            Assert.Contains(f.Repo.GetRelationPropertyOptions(f.Collection), option => option.Key == computed.ToString());
            Assert.Contains(f.Repo.GetAutoRelationPropertyOptions(f.Collection), option => option.Key == computed.ToString());
            var raw = f.Repo.GetRelationComputedRawValues([source], f.Repo.GetFields(f.Collection).Single(field => field.Id == computed));
            Assert.Equal("7200", raw[source].Raw);
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSQL_recalculation_rereads_metadata_after_a_concurrent_type_change(bool changeSourceType)
    {
        using var f = new Fixture(postgres: true);
        var id = f.Create("Конкурентная правка", "2026-10-28T09:00", "2026-10-28T11:00");
        f.Repo.SetFieldValue(id, f.Start, "2026-10-28T09:00/2026-10-28T13:00");
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
                    change.Parameters.AddWithValue(changeSourceType ? f.Start : f.Fixed);
                    Assert.Equal(1, await change.ExecuteNonQueryAsync());
                }
                recalculation = Task.Run<Exception?>(() => Record.Exception(() => worker.Constructor.RecalculateDatePeriod(id, f.Fixed)));
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

        f.Repo.SetFieldValue(id, f.Start, "2026-10-28T09:00/2026-10-28T13:00");
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
    public void Screenshot_date_ranges_use_their_own_end_and_the_configured_work_window(bool postgres)
    {
        using var f = new Fixture(postgres);
        var period = f.Repo.GetFields(f.Collection).Single(field => field.Id == f.Automatic);
        period.ConfigJson = new DatePeriodConfig(f.Start, DayStart: "09:00", DayEnd: "18:00").ToConfigJson();
        f.Repo.UpdateField(period);
        foreach (var (dates, seconds) in new[]
        {
            ("2026-09-01/2026-09-04", "97200"),
            ("2026-09-02/2026-09-03", "32400"),
            ("2026-09-02/2026-09-04", "64800"),
            ("2026-09-21/2026-10-27", "1166400"),
            ("2026-09-04", "0"),
        })
        {
            var id = Assert.Single(f.Repo.BulkAddRecords(f.Collection, [f.Start, f.End],
                [new BulkRecordRow(dates, [dates, "2035-12-31"])], 1, default));
            Assert.Equal(seconds, f.Value(id, f.Automatic));
            Assert.Equal(seconds, f.Repo.GetFieldValuesBatch([id], [f.Automatic])[id][f.Automatic]);
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Legacy_configs_use_the_start_fields_range_and_ignore_the_old_end_field_or_current_time_mode(bool postgres)
    {
        using var f = new Fixture(postgres);
        var oldTwoDates = f.Repo.AddField(new DbField
        {
            CollectionId = f.Collection, Name = "Старый выбор двух дат", FieldType = "date_period", IsSystem = true,
            ConfigJson = JsonSerializer.Serialize(new { datePeriod = new { version = 1, startFieldId = f.Start,
                endFieldId = f.End, automatic = true, weekdays = 127, dayStart = "09:00", dayEnd = "18:00" } }),
        }).Id!.Value;
        var oldNow = f.Repo.AddField(new DbField
        {
            CollectionId = f.Collection, Name = "Старый режим текущей даты", FieldType = "date_period", IsSystem = true,
            ConfigJson = JsonSerializer.Serialize(new { datePeriod = new { startFieldId = f.Start,
                automatic = true, weekdays = 127, dayStart = "09:00", dayEnd = "18:00" } }),
        }).Id!.Value;
        var id = Assert.Single(f.Repo.BulkAddRecords(f.Collection, [f.Start, f.End],
            [new BulkRecordRow("Реальный диапазон", ["2026-09-01/2026-09-04", "2040-01-01"])], 1, default));
        foreach (var field in new[] { oldTwoDates, oldNow }) Assert.Equal("97200", f.Value(id, field));
        f.Repo.SetFieldValue(id, f.End, "1900-01-01");
        foreach (var field in new[] { oldTwoDates, oldNow }) Assert.Equal("97200", f.Value(id, field));
        f.Repo.SetFieldValue(id, f.Start, "2026-09-02/2026-09-03");
        foreach (var field in new[] { oldTwoDates, oldNow }) Assert.Equal("32400", f.Value(id, field));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Automatic_range_filter_snapshot_is_reused_until_source_dates_change(bool postgres)
    {
        using var f = new Fixture(postgres);
        var first = f.Create("Первая", "2026-10-28T07:00", "2026-10-28T09:00");
        var second = f.Create("Вторая", "2026-10-28T07:00", "2026-10-28T09:00");
        SampleData.AddRecord(f.Repo, f.Collection, "Без даты");
        var key = f.Automatic.ToString(CultureInfo.InvariantCulture);
        var initial = await f.Repo.GetColumnValueListAsync(f.Collection, key);
        Assert.NotNull(initial.Snapshot);
        Assert.Contains(initial.Values, value => value.Value == "7200" && value.Count == 2);
        Assert.Contains(initial.Values, value => value.Value.Length == 0 && value.Count == 1);
        var reused = await f.Repo.GetColumnValueListAsync(f.Collection, key, reuse: initial.Snapshot);
        Assert.Same(initial.Snapshot, reused.Snapshot);
        Assert.Equal(initial.Values, reused.Values);
        var counts = await f.Repo.GetColumnCountsAsync(f.Collection, key);
        Assert.Contains(counts, value => value.Value == "7200" && value.Count == 2);
        f.Repo.SetFieldValue(first, f.Start, "2026-10-28T07:00/2026-10-28T10:00");
        var changed = await f.Repo.GetColumnValueListAsync(f.Collection, key);
        Assert.Contains(changed.Values, value => value.Value == "7200" && value.Count == 1);
        Assert.Contains(changed.Values, value => value.Value == "10800" && value.Count == 1);
        var searched = await f.Repo.GetColumnValueListAsync(f.Collection, key, search: "3 ч", reuse: changed.Snapshot);
        Assert.Same(changed.Snapshot, searched.Snapshot);
        Assert.Equal([("10800", 1)], searched.Values);
        var filtered = await f.Repo.GetFilteredRecordIdsAsync(f.Collection,
            new Dictionary<string, HashSet<string>> { [key] = ["7200"] }, false, key, true, "date_period", default);
        Assert.Equal([second], filtered);
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
        Assert.Equal("0", f.Value(id, f.Automatic));
        f.Repo.SetFieldValue(id, f.End, "2026-10-29");
        Assert.Equal("0", f.Value(id, f.Automatic));
        f.Repo.SetFieldValue(id, f.Start, "2026-10-28/2026-10-29");
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
            f.Repo.SetFieldValue(created, f.Start, "2026-10-28T09:00/2026-10-28T12:00");
            f.Repo.SetFieldValue(created, f.End, "2026-10-28T12:00");
            return created;
        });
        Assert.Equal("10800", f.Value(id, f.Fixed));
        f.Repo.SetFieldValue(id, f.Start, "2026-10-28T10:00/2026-10-28T12:00");
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
                new DbFieldValueEntity { FieldId = f.Start, Value = "2026-10-28T09:00/2026-10-28T12:00" },
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
        f.Repo.SetFieldValue(record.Id, f.Start, "2026-10-28T09:00/2026-10-28T14:00");
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
                f.Repo.SetFieldValue(id, f.Start, "2026-10-28T09:00/" + end);
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
        f.Repo.SetFieldValue(first, f.Start, "2026-10-28T09:00/2026-10-28T15:00");
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
            [new BulkRecordRow("Из внешних данных", ["2026-10-28T09:00/2026-10-28T12:00", "2026-10-28T12:00", "999", "999"])], 1, default));
        Assert.Equal("10800", f.Value(created, f.Automatic));
        Assert.Equal("10800", f.Value(created, f.Fixed));
        Assert.False(f.Manager.Context.DbFieldValues.Any(v => v.FieldId == f.Automatic));
        Assert.DoesNotContain(f.Manager.Context.DbFieldValues.AsNoTracking().Where(v => v.FieldId == f.Fixed), v => v.Value == "999");
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bulk_recalculation_changes_only_selected_rows_and_supports_a_later_single_refresh(bool postgres)
    {
        using var f = new Fixture(postgres);
        var first = f.Create("Первая", "2026-10-28T09:00", "2026-10-28T11:00");
        var second = f.Create("Вторая", "2026-10-28T09:00", "2026-10-28T11:00");
        foreach (var id in new[] { first, second }) f.Repo.SetFieldValue(id, f.Start, "2026-10-28T09:00/2026-10-28T13:00");
        f.Repo.RecalculateDatePeriods([first], [f.Fixed]);
        Assert.Equal("14400", f.Value(first, f.Fixed));
        Assert.Equal("7200", f.Value(second, f.Fixed));
        f.Repo.RecalculateDatePeriods([first, second]);
        Assert.Equal("14400", f.Value(second, f.Fixed));

        f.Repo.SetFieldValue(second, f.Start, "2026-10-28T09:00/2026-10-28T14:00");
        f.Repo.RecalculateDatePeriod(second, f.Fixed);
        Assert.Equal("18000", f.Value(second, f.Fixed));
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
    public void Copying_a_collection_remaps_the_date_range_source_and_preserves_the_fixed_snapshot(bool postgres)
    {
        using var f = new Fixture(postgres);
        var source = f.Create("Первая", "2026-10-28T09:00", "2026-10-28T11:00");
        f.Repo.SetFieldValue(source, f.Start, "2026-10-28T09:00/2026-10-28T13:00");
        var collection = f.Repo.DuplicateCollection(f.Collection).Id!.Value;
        var fields = f.Repo.GetFields(collection).ToDictionary(field => field.Name);
        var automatic = fields["Автоматический период"];
        var fixedField = fields["Фиксированный период"];
        foreach (var field in new[] { automatic, fixedField })
        {
            var config = DatePeriodConfig.FromConfig(field.ConfigJson);
            Assert.NotNull(config);
            Assert.Equal(fields["Начало"].Id, config!.DateFieldId);
            Assert.NotEqual(f.Start, config.DateFieldId);
        }
        var copied = Assert.Single(f.Repo.GetRecords(collection)).Id!.Value;
        Assert.Equal("14400", f.Value(copied, automatic.Id!.Value));
        Assert.Equal("7200", f.Value(copied, fixedField.Id!.Value));
        f.Repo.SetFieldValue(copied, fields["Начало"].Id!.Value, "2026-10-28T09:00/2026-10-28T14:00");
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
        update.ConfigJson = new DatePeriodConfig(foreignDate).ToConfigJson();
        Assert.Throws<InvalidOperationException>(() => f.Repo.UpdateField(update));
        var restored = DatePeriodConfig.FromConfig(f.Repo.GetFields(f.Collection).Single(field => field.Id == f.Automatic).ConfigJson);
        Assert.Equal(f.Start, restored!.DateFieldId);
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
            ConfigJson = new DatePeriodConfig(Start, Automatic: automatic).ToConfigJson(),
        }).Id!.Value;

        public int Create(string title, string start, string end) => Assert.Single(Repo.BulkAddRecords(Collection, [Start, End],
            [new BulkRecordRow(title, [start + "/" + end, end])], 1, default));

        public string Value(int record, int field) => Repo.GetFieldValues(record).GetValueOrDefault(field, "");

        public void Dispose()
        {
            if (_sqlite is not null) _sqlite.Dispose();
            else Manager.Dispose();
        }
    }
}
