using Microsoft.EntityFrameworkCore;
using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

public sealed class RegistrationReservationSafetyTests
{
    [Fact]
    public void Import_refuses_an_occupied_designation_and_rolls_back_earlier_rows()
    {
        using var database = new TempDatabase("registration-import-reservation");
        var collection = JournalStand.Create(database.Constructor);
        var shape = JournalStand.Shape(database.Constructor, collection);
        var now = new DateTime(2026, 10, 26);
        var registered = DocRegistrar.Register(database.Constructor, shape,
            new RegistrationInput("Первый", "АБВГ", "301121", "Исполнитель", "Проект"), now);
        var logBefore = database.Context.ChangeLog.AsNoTracking().Count();
        var rows = new[]
        {
            new ImportRow(2, "Новый", "АБВГ.301121.002", "Исполнитель", "Проект", "", "", ""),
            new ImportRow(3, "Конфликт", registered.Designation, "Исполнитель", "Проект", "", "", ""),
        };

        var error = Assert.Throws<DocRegistryException>(() =>
            DocRegistrar.RegisterAll(database.Constructor, shape, rows, now));

        Assert.Contains("уже зарегистрировано", error.Message);
        Assert.Equal(registered.RecordId, Assert.Single(database.Constructor.GetRecords(collection)).Id);
        Assert.Equal(logBefore, database.Context.ChangeLog.AsNoTracking().Count());
        Assert.Equal([registered.Designation], DocRegistrar.Designations(database.Constructor, shape));
    }
}
