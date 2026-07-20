// Интеграционные тесты гоняются последовательно: TempDatabase.Dispose вызывает
// SqliteConnection.ClearAllPools() — процесс-глобальную очистку пула соединений, из-за
// которой параллельные тесты изредка задевают чужие подключения (флак). Unit-тесты
// остаются параллельными (чистые, без общего состояния).
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
