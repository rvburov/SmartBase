using SmartBase.Tests.Benchmarks;

// Диспетчер бенчмарков: первая позиция — что мерить, остальное — опции конкретного замера.
if (args.Length == 0)
{
    Console.WriteLine("Использование: dotnet run -c Release --project Tests/Benchmarks -- <команда> [опции]");
    Console.WriteLine("  fileauto [--files N] [--exact]                 — дисковый каталог синтетических путей (по умолчанию 10 млн)");
    Console.WriteLine("  sidebar [--db PATH] [--target PATH] [--light]  — операции сайдбара на большом стенде");
    Console.WriteLine("  import  [--rows N] [--dir PATH]                — замер импорта CSV");
    Console.WriteLine("  export  [--rows N] [--dir PATH]                — замер экспорта во все форматы");
    Console.WriteLine("  pgload  [клиентов] [секунд] [--db ИМЯ]         — нагрузка PostgreSQL (стенд sb-pg)");
    Console.WriteLine("  pgperf  [--rows N] [--db ИМЯ]                  — производительность PostgreSQL: заливка/страницы/фильтры (стенд sb-pg)");
    Console.WriteLine("  csvlog  [--rows N] [--dir PATH]                — выгрузка журнала и диагностики в CSV");
    Console.WriteLine("  dbsize  --db PATH [--top N] [--fast]           — из чего состоит файл SQLite (только чтение)");
    Console.WriteLine("  bloat   [--count N] [--dir PATH]               — сценарий раздувания файла: заливка/удаление/правки/VACUUM");
    Console.WriteLine("  delete  --db PATH [--batch N] [--col ИМЯ]      — удаление большой коллекции: время, темп, файл и -wal, память");
    Console.WriteLine("  dup     --db PATH [--col ИМЯ] [--batch N] [--keep] — дубль большой коллекции и удаление копии");
    Console.WriteLine("  pgdelete [--rows N] [--db ИМЯ]                 — то же удаление на PostgreSQL (стенд sb-pg)");
    Console.WriteLine("  wide    --db PATH [--columns N]                — цена широкой таблицы в ядре: снимок открытия, страницы, значения");
    Console.WriteLine("  docread [--count N] [--pages N] [--dir PATH] [--csv PATH] — разбор, FTS и открытие XLSX (библиотека документов)");
    Console.WriteLine("  docfeed [--docs N] [--cols N] [--files N] [--root DIR] [--db PATH] — лента библиотеки: счет, страницы, перепись кеша");
    Console.WriteLine("  backup  --db PATH [--network]                  — резервная копия: файл проводником, прежняя и нынешняя копия");
    Console.WriteLine("  pgmove  --db PATH [--pg ИМЯ] [--col ИМЯ]       — перенос коллекции SQLite → PostgreSQL и обратно (стенд sb-pg)");
    return 1;
}

var rest = args.Skip(1).ToArray();
return args[0] switch
{
    "fileauto" => FileAutoBench.Run(rest),
    "sidebar" => SidebarBench.Run(rest),
    "import"  => ImportBench.Run(rest),
    "export"  => ExportBench.Run(rest),
    "pgload"  => PgLoadBench.Run(rest),
    "pgperf"  => PgPerfBench.Run(rest),
    "csvlog"  => LogExportBench.Run(rest),
    "dbsize"  => DbSizeBench.Run(rest),
    "bloat"   => BloatBench.Run(rest),
    "delete"  => DeleteBench.Run(rest),
    "dup"     => DupBench.Run(rest),
    "pgdelete" => PgDeleteBench.Run(rest),
    "wide"    => WideBench.Run(rest),
    "docread" => DocReadBench.Run(rest),
    "docfeed" => DocFeedBench.Run(rest),
    "backup"  => BackupBench.Run(rest),
    "pgmove"  => PgCrossMoveBench.Run(rest),
    _         => Unknown(args[0]),
};

static int Unknown(string cmd)
{
    Console.WriteLine($"Неизвестная команда: {cmd}. Доступно: sidebar | import | export | pgload | pgperf | " +
                      "fileauto | csvlog | dbsize | bloat | delete | pgdelete | wide | docread | docfeed | backup | pgmove | dup.");
    return 1;
}
