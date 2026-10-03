using SmartBase.Utils;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Поиск установок PostgreSQL на Windows (<see cref="PgWindowsInstalls"/>) — его чистые части:
/// папка сервера по пути запуска службы, порядок версий и слияние находок одной папки из разных
/// источников. Сам реестр здесь не трогается: тесты идут и на Linux. Что поиск видит сервер вне
/// «Program Files» на живой машине, показывают <c>PgBackupTests</c>: на рабочей станции они
/// пропускались, пока pg_dump искался только там.
/// </summary>
public sealed class PgWindowsInstallsTests
{
    [Theory]
    // Служба установщика: путь в кавычках, дальше аргументы pg_ctl.
    [InlineData("\"C:\\Programs\\PostgreSQL\\18\\bin\\pg_ctl.exe\" runservice -N \"postgresql-x64-18\" -D \"C:\\Programs\\PostgreSQL\\18\\data\" -w",
                "C:\\Programs\\PostgreSQL\\18")]
    // Пробел внутри пути — только в кавычках.
    [InlineData("\"C:\\Program Files\\PostgreSQL\\16\\bin\\pg_ctl.exe\" runservice -N \"postgresql-x64-16\"",
                "C:\\Program Files\\PostgreSQL\\16")]
    // pg_ctl register без кавычек; служба может запускать и сам postgres.exe.
    [InlineData("C:\\pgsql\\bin\\pg_ctl.exe runservice -N PostgreSQL -D C:\\pgsql\\data", "C:\\pgsql")]
    [InlineData("C:\\pgsql\\BIN\\Postgres.EXE -D C:\\pgsql\\data", "C:\\pgsql")]
    public void Service_path_gives_the_server_folder(string imagePath, string expected) =>
        Assert.Equal(expected, PgWindowsInstalls.BaseDirOfService(imagePath));

    [Theory]
    [InlineData("\"C:\\Windows\\system32\\svchost.exe\" -k netsvcs")]   // чужая служба
    [InlineData("\"C:\\tools\\pg_ctl.exe\" runservice")]                 // не в bin — папку сервера не угадать
    [InlineData("\"C:\\Programs\\PostgreSQL\\18\\bin\\pg_ctl.exe")]      // кавычка не закрыта
    [InlineData("C:\\pgsql\\bin\\pg_ctl")]                               // без .exe
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_not_a_server(string? imagePath) =>
        Assert.Null(PgWindowsInstalls.BaseDirOfService(imagePath));

    [Fact]
    public void Versions_order_by_number_not_by_text()
    {
        // Текстом «9.6» больше «18», числом — меньше.
        Assert.True(PgWindowsInstalls.VersionKey("18") > PgWindowsInstalls.VersionKey("9.6"));
        Assert.True(PgWindowsInstalls.VersionKey("18.6-3") > PgWindowsInstalls.VersionKey("18.6"));
        Assert.True(PgWindowsInstalls.VersionKey("18.6") > PgWindowsInstalls.VersionKey("17.2"));
        Assert.Equal(new Version(16, 0), PgWindowsInstalls.VersionKey("16"));
        Assert.Equal(new Version(0, 0),  PgWindowsInstalls.VersionKey(""));
        Assert.Equal(new Version(0, 0),  PgWindowsInstalls.VersionKey(null));
    }

    /// <summary>
    /// Одна папка, найденная двумя источниками, — одна установка. Описание — из первого (реестр
    /// установщика надежнее), а службу, которой в нем нет, дает следующий. Регистр и косая черта
    /// в конце пути одну папку не делят надвое.
    /// </summary>
    [Fact]
    public void One_folder_found_twice_is_one_install_with_the_best_details()
    {
        var merged = PgWindowsInstalls.Merge(
        [
            new(@"C:\Programs\PostgreSQL\18",  "18.6-3", null),
            new(@"c:\programs\postgresql\18\", "18.6",   "postgresql-x64-18"),
        ]);

        var one = Assert.Single(merged);
        Assert.Equal(@"C:\Programs\PostgreSQL\18", one.BaseDir);
        Assert.Equal("18.6-3", one.Version);
        Assert.Equal("postgresql-x64-18", one.Service);
    }

    [Fact]
    public void Newest_install_comes_first()
    {
        var merged = PgWindowsInstalls.Merge(
        [
            new(@"C:\Program Files\PostgreSQL\9.6", "9.6",  "postgresql-x64-9.6"),
            new(@"C:\Programs\PostgreSQL\18",       "18.6", "postgresql-x64-18"),
            new(@"D:\pgsql",                        "17.2", "PostgreSQL"),
        ]);

        Assert.Equal(["18.6", "17.2", "9.6"], merged.Select(i => i.Version).ToList());
    }
}
