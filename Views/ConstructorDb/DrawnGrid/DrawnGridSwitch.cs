namespace SmartBase.Views.ConstructorDb.DrawnGrid;

/// <summary>
/// Переключатель разработчика на время переезда таблицы (этапы 2–5 плана drawn-grid.md):
/// переменная среды <c>SMARTBASE_DRAWN_GRID=1</c> подменяет в <c>TableView</c> нынешнюю сетку
/// рисованной, чтобы прогонять чеклисты этапов в настоящей программе. Настройки в интерфейсе нет
/// (решение пользователя: старая таблица удаляется по паритету, переключатель не нужен); на
/// этапе 6 переключатель удаляется вместе с нынешней сеткой.
/// </summary>
public static class DrawnGridSwitch
{
    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("SMARTBASE_DRAWN_GRID") is "1" or "true" or "True";
}
