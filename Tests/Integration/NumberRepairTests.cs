using System.Data.Common;
using Microsoft.Data.Sqlite;
using SmartBase.Core.Models;
using SmartBase.Database;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Разовый ремонт чисел, записанных показом (<c>DatabaseManager.RepairFormattedNumbers</c>): до
/// 15.09.2026 панель записи писала в поле «Число» с форматом показа отформатированную строку
/// вместо числа. Монтирование приводит такие значения к канону хранения, текст, который числом
/// не был, не трогает, и помечает файл сделанным.
/// </summary>
public sealed class NumberRepairTests
{
    [Fact]
    public void Values_written_as_display_are_repaired_on_mount()
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            int money, plain, r1, r2;
            using (var m = DatabaseManager.Open(1, "old", path))
            {
                var repo  = m.Constructor;
                var col   = SampleData.AddCollection(repo).Id!.Value;
                var field = repo.AddField(new DbField
                {
                    CollectionId = col, Name = "Цена", FieldType = "number", Position = 10,
                    ConfigJson   = "{\"numberFormat\":\"currency\",\"decimals\":2,\"thousands\":true}",
                });
                money = field.Id!.Value;
                plain = SampleData.AddField(repo, col, "Количество", "number", 20).Id!.Value;
                r1    = SampleData.AddRecord(repo, col, "R1", 1000).Id!.Value;
                r2    = SampleData.AddRecord(repo, col, "R2", 2000).Id!.Value;
                repo.SetFieldValue(r1, money, "1234567,5");
                repo.SetFieldValue(r1, plain, "7");
                repo.SetFieldValue(r2, money, "-3");
            }

            using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                raw.Open();
                // То, что оставляла панель: показ по формату поля вместо числа.
                Exec(raw, $"UPDATE db_field_values SET Value = '1 234 567,50 ₽' WHERE RecordId = {r1} AND FieldId = {money};");
                Exec(raw, $"UPDATE db_field_values SET Value = '(3,00 ₽)' WHERE RecordId = {r2} AND FieldId = {money};");
                // Текст, который числом не был (после смены типа из текстового), — не трогается.
                Exec(raw, $"UPDATE db_field_values SET Value = 'семь' WHERE RecordId = {r1} AND FieldId = {plain};");
                // Первое монтирование выше уже пометило ремонт сделанным — старый файл этой
                // версией еще не открывался, снимаем флаг.
                Exec(raw, "DELETE FROM db_meta WHERE Key = 'repair.number-display';");
            }
            SqliteConnection.ClearAllPools();

            // Монтирование: фоновый ремонт (Dispose его дожидается) и флаг «сделано».
            using (DatabaseManager.Open(1, "old", path)) { }

            using var check = new SqliteConnection($"Data Source={path};Pooling=False");
            check.Open();
            Assert.Equal("1234567,50", Scalar(check, $"SELECT Value FROM db_field_values WHERE RecordId = {r1} AND FieldId = {money};"));
            Assert.Equal("-3,00",      Scalar(check, $"SELECT Value FROM db_field_values WHERE RecordId = {r2} AND FieldId = {money};"));
            Assert.Equal("семь",       Scalar(check, $"SELECT Value FROM db_field_values WHERE RecordId = {r1} AND FieldId = {plain};"));
            Assert.Equal("1",          Scalar(check, "SELECT Value FROM db_meta WHERE Key = 'repair.number-display';"));
        }
        finally { TempDatabase.Cleanup(path); }
    }

    /// <summary>Здоровый файл ремонт не трогает: значения те же, флаг стоит.</summary>
    [Fact]
    public void Healthy_values_are_left_alone()
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            int money, r1;
            using (var m = DatabaseManager.Open(1, "ok", path))
            {
                var repo  = m.Constructor;
                var col   = SampleData.AddCollection(repo).Id!.Value;
                money = repo.AddField(new DbField
                {
                    CollectionId = col, Name = "Доля", FieldType = "number", Position = 10,
                    ConfigJson   = "{\"numberFormat\":\"percent\",\"decimals\":1}",
                }).Id!.Value;
                r1 = SampleData.AddRecord(repo, col, "R1", 1000).Id!.Value;
                repo.SetFieldValue(r1, money, "12,5");
            }
            using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                raw.Open();
                Exec(raw, "DELETE FROM db_meta WHERE Key = 'repair.number-display';");
            }
            SqliteConnection.ClearAllPools();

            using (DatabaseManager.Open(1, "ok", path)) { }

            using var check = new SqliteConnection($"Data Source={path};Pooling=False");
            check.Open();
            Assert.Equal("12,5", Scalar(check, $"SELECT Value FROM db_field_values WHERE RecordId = {r1} AND FieldId = {money};"));
            Assert.Equal("1",    Scalar(check, "SELECT Value FROM db_meta WHERE Key = 'repair.number-display';"));
        }
        finally { TempDatabase.Cleanup(path); }
    }

    private static void Exec(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
