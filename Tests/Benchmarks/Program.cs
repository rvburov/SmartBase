using SmartBase.Tests.Benchmarks;

// Диспетчер бенчмарков: первая позиция — что мерить, остальное — опции конкретного замера.
if (args.Length == 0)
{
    Console.WriteLine("Использование: dotnet run -c Release --project Tests/Benchmarks -- <команда> [опции]");
    Console.WriteLine("  sidebar [--db PATH] [--target PATH] [--light]  — операции сайдбара на большом стенде");
    Console.WriteLine("  import  [--rows N] [--dir PATH]                — замер импорта CSV");
    Console.WriteLine("  export  [--rows N] [--dir PATH]                — замер экспорта во все форматы");
    Console.WriteLine("  pgload  [клиентов] [секунд] [--db ИМЯ]         — нагрузка PostgreSQL (стенд sb-pg)");
    Console.WriteLine("  csvlog  [--rows N] [--dir PATH]                — выгрузка журнала и диагностики в CSV");
    return 1;
}

var rest = args.Skip(1).ToArray();
return args[0] switch
{
    "sidebar" => SidebarBench.Run(rest),
    "import"  => ImportBench.Run(rest),
    "export"  => ExportBench.Run(rest),
    "pgload"  => PgLoadBench.Run(rest),
    "csvlog"  => LogExportBench.Run(rest),
    _         => Unknown(args[0]),
};

static int Unknown(string cmd)
{
    Console.WriteLine($"Неизвестная команда: {cmd}. Доступно: sidebar | import | export | pgload | csvlog.");
    return 1;
}
