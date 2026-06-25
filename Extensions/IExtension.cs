namespace DataBase.Extensions;

/// <summary>
/// Контракт самостоятельного расширения. Каждое расширение лежит в своей папке
/// под <c>Extensions/&lt;Имя&gt;/</c> и НИ ОТ ЧЕГО в приложении не зависит, кроме
/// этого интерфейса — оболочка (сайдбар + блок) знает о расширении только через него.
/// </summary>
public interface IExtension
{
    /// <summary>Стабильный идентификатор (ключ строки в сайдбаре).</summary>
    string Id { get; }

    /// <summary>Отображаемое имя — в навбаре блока и в строке сайдбара.</summary>
    string Name { get; }

    /// <summary>Описание расширения в формате Markdown — тело блока.</summary>
    string MarkdownDescription { get; }

    /// <summary>Можно ли запустить расширение прямо сейчас (управляет кнопкой в навбаре).</summary>
    bool CanRun { get; }

    /// <summary>Запуск расширения. Заглушки могут не делать ничего.</summary>
    void Run();
}
