// Интеграционные тесты гоняются последовательно: TempDatabase.Dispose вызывает
// SqliteConnection.ClearAllPools() — процесс-глобальную очистку пула соединений, из-за
// которой параллельные тесты изредка задевают чужие подключения (флак). Модульные тесты
// последовательны по своей причине — статика AppSettings.AttachStore, см. Tests/Unit/AssemblyInfo.cs.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
