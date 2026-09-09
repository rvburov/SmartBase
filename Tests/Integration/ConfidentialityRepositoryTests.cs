using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Метки конфиденциальности в базе: метка базы лежит в <c>db_meta</c> (внутри самого файла),
/// метка свойства — в его <c>ConfigJson</c>.
///
/// Два теста здесь важнее прочих. Первый — что метка базы **уезжает вместе с файлом**: храни
/// ее реестр баз в <c>app.db</c>, и «закрытый контур» снимался бы копированием файла на
/// соседний компьютер. Второй — что из закрытой базы данные не переезжают в открытую: без
/// этого запрет на исходящие обходится в два приема, перенес и выгрузил уже оттуда.
///
/// Что метки значат — Core/Models/Confidentiality.cs; кто на них смотрит перед отправкой —
/// Integrations/EgressGate.cs.
/// </summary>
public sealed class ConfidentialityRepositoryTests
{
    // ── Метка базы ────────────────────────────────────────────────────────────

    [Fact]
    public void Database_without_a_label_is_open()
    {
        using var db = new TempDatabase();

        Assert.Equal(ConfidentialityLabel.Open, db.Constructor.GetDatabaseLabel());
    }

    [Fact]
    public void Database_label_round_trips()
    {
        using var db = new TempDatabase();

        db.Constructor.SetDatabaseLabel(ConfidentialityLabel.Closed);
        Assert.Equal(ConfidentialityLabel.Closed, db.Constructor.GetDatabaseLabel());

        db.Constructor.SetDatabaseLabel(ConfidentialityLabel.Restricted);
        Assert.Equal(ConfidentialityLabel.Restricted, db.Constructor.GetDatabaseLabel());

        // «Открыто» — не значение, а его отсутствие: строка убирается.
        db.Constructor.SetDatabaseLabel(ConfidentialityLabel.Open);
        Assert.Equal(ConfidentialityLabel.Open, db.Constructor.GetDatabaseLabel());
        using var ctx = new AppDbContext(db.Path);
        Assert.False(ctx.DbMeta.AsNoTracking().Any(m => m.Key == Confidentiality.Key));
    }

    /// <summary>
    /// Метка лежит ВНУТРИ файла базы и потому уезжает вместе с ним. Проверяется отдельным
    /// подключением к тому же файлу — так же, как базу увидит другой компьютер, открыв ее из
    /// общей папки.
    /// </summary>
    [Fact]
    public void Database_label_travels_inside_the_file()
    {
        using var db = new TempDatabase();
        db.Constructor.SetDatabaseLabel(ConfidentialityLabel.Closed);

        using var reopened = new AppDbContext(db.Path);
        var stored = reopened.DbMeta.AsNoTracking().First(m => m.Key == Confidentiality.Key).Value;

        Assert.Equal(ConfidentialityLabel.Closed, Confidentiality.Parse(stored));
    }

    /// <summary>
    /// Смена метки попадает в журнал изменений, а понижение — отдельным кодом: именно его
    /// ищут, когда разбираются, как данные ушли наружу. Снять метку незаметно не выйдет.
    /// </summary>
    [Fact]
    public async Task Label_change_is_written_to_the_change_log()
    {
        using var db = new TempDatabase();

        db.Constructor.SetDatabaseLabel(ConfidentialityLabel.Closed);
        db.Constructor.SetDatabaseLabel(ConfidentialityLabel.Open);

        var rows = await db.Constructor.GetChangeLogPageAsync(
            new SmartBase.Database.Repositories.ConstructorRepository.ChangeLogQuery(), take: 100);

        Assert.Contains(rows, r => r.Action == "label"       && r.TargetType == "database");
        Assert.Contains(rows, r => r.Action == "label.lower" && r.TargetType == "database");
    }

    // ── Метка свойства ────────────────────────────────────────────────────────

    [Fact]
    public void Field_label_round_trips_and_keeps_other_settings()
    {
        using var db = new TempDatabase();
        var col   = SampleData.AddCollection(db.Constructor, "Заказы");
        var field = SampleData.AddField(db.Constructor, col.Id!.Value, "Сумма", "number");

        field.ConfigJson = """{"format":"0.00"}""";
        db.Constructor.UpdateField(field);

        db.Constructor.SetFieldLabel(field.Id!.Value, ConfidentialityLabel.Closed);

        Assert.Equal(ConfidentialityLabel.Closed, db.Constructor.GetFieldLabel(field.Id!.Value));

        var config = db.Constructor.GetFields(col.Id!.Value).Single(f => f.Id == field.Id).ConfigJson;
        Assert.Contains("0.00", config, StringComparison.Ordinal);
    }

    /// <summary>
    /// Список помеченных свойств нужен экрану настройки привязки: такое поле не должно
    /// появляться в списке вовсе — узнавать об ограничении из ошибки в журнале человек не должен.
    /// </summary>
    [Fact]
    public void Labeled_fields_are_listed_for_the_mapping_screen()
    {
        using var db = new TempDatabase();
        var col    = SampleData.AddCollection(db.Constructor, "Изделия");
        var open   = SampleData.AddField(db.Constructor, col.Id!.Value, "Телефон", "text");
        var closed = SampleData.AddField(db.Constructor, col.Id!.Value, "Состав", "text");

        db.Constructor.SetFieldLabel(closed.Id!.Value, ConfidentialityLabel.Closed);

        var labeled = db.Constructor.LabeledFieldIds(col.Id!.Value);

        Assert.Equal([closed.Id!.Value], labeled);
        Assert.DoesNotContain(open.Id!.Value, labeled);
    }

    // ── Перенос между базами ──────────────────────────────────────────────────

    /// <summary>
    /// Из закрытого контура в обычную базу перенос запрещен. Иначе запрет на исходящие
    /// обходится в два приема: перенес коллекцию, а выгрузил уже из открытой базы.
    /// </summary>
    [Fact]
    public void Move_out_of_a_closed_database_is_refused()
    {
        using var src = new TempDatabase(dbId: 1, name: "src");
        using var tgt = new TempDatabase(dbId: 2, name: "tgt");

        var col = SampleData.AddCollection(src.Constructor, "Секретное");
        SampleData.AddRecord(src.Constructor, col.Id!.Value, "запись", 10);
        src.Constructor.SetDatabaseLabel(ConfidentialityLabel.Closed);

        var error = Assert.Throws<InvalidOperationException>(() =>
            tgt.Constructor.MoveCollectionIsolated(src.Path, col.Id!.Value, targetGroupId: null));

        Assert.Contains("закрытым контуром", error.Message, StringComparison.Ordinal);

        // Источник цел: отказ происходит до единой правки.
        Assert.Equal(1, src.Constructor.CountRecords(col.Id!.Value));
    }

    /// <summary>В такую же закрытую базу перенос разрешен: контур не покидается.</summary>
    [Fact]
    public void Move_between_two_closed_databases_is_allowed()
    {
        using var src = new TempDatabase(dbId: 1, name: "src");
        using var tgt = new TempDatabase(dbId: 2, name: "tgt");

        var col = SampleData.AddCollection(src.Constructor, "Секретное");
        SampleData.AddRecord(src.Constructor, col.Id!.Value, "запись", 10);
        src.Constructor.SetDatabaseLabel(ConfidentialityLabel.Closed);
        tgt.Constructor.SetDatabaseLabel(ConfidentialityLabel.Closed);

        var moved = tgt.Constructor.MoveCollectionIsolated(src.Path, col.Id!.Value, targetGroupId: null);

        Assert.NotEqual(0, moved);
        Assert.Equal(1, tgt.Constructor.CountRecords(moved));
    }

    /// <summary>Обычные базы переносом не задеты — запрет ровно один и узкий.</summary>
    [Fact]
    public void Move_between_open_databases_still_works()
    {
        using var src = new TempDatabase(dbId: 1, name: "src");
        using var tgt = new TempDatabase(dbId: 2, name: "tgt");

        var col = SampleData.AddCollection(src.Constructor, "Обычное");
        SampleData.AddRecord(src.Constructor, col.Id!.Value, "запись", 10);

        var moved = tgt.Constructor.MoveCollectionIsolated(src.Path, col.Id!.Value, targetGroupId: null);

        Assert.NotEqual(0, moved);
    }
}
