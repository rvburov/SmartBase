namespace SmartBase.Utils;

/// <summary>Чистая логика поведения окна в фоновом режиме — вынесена, чтобы покрыть тестом.</summary>
public static class BackgroundBehavior
{
    /// <summary>
    /// Закрытие окна должно СКРЫТЬ его (а не завершить приложение), если включен фоновый режим,
    /// это не явный выход (трей «Выход» / ⌘Q / Ctrl+Q) и есть чем вернуть окно обратно.
    /// </summary>
    /// <param name="canReturn">
    /// Значок в системной области создан. Без него прятать окно нельзя: программа исчезнет
    /// с экрана, а точки возврата не будет — останутся только клик по уведомлению и повторный
    /// запуск, о которых человек не догадывается. Лучше честно закрыться.
    /// </param>
    public static bool ShouldHideOnClose(bool backgroundEnabled, bool explicitQuit, bool canReturn) =>
        backgroundEnabled && !explicitQuit && canReturn;
}
