namespace SmartBase.Utils;

/// <summary>Чистая логика поведения окна в фоновом режиме — вынесена, чтобы покрыть тестом.</summary>
public static class BackgroundBehavior
{
    /// <summary>Закрытие окна должно СКРЫТЬ его (а не завершить приложение), если включён
    /// фоновый режим и это не явный выход (трей «Выход» / ⌘Q / Ctrl+Q).</summary>
    public static bool ShouldHideOnClose(bool backgroundEnabled, bool explicitQuit) =>
        backgroundEnabled && !explicitQuit;
}
