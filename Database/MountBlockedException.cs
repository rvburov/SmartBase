namespace SmartBase.Database;

/// <summary>
/// Монтирование не состоялось по понятной внешней причине, которую стоит показать пользователю
/// словами, а не «не удалось открыть»: файл общей базы в режиме WAL держит другой компьютер
/// (перевод в DELETE требует эксклюзивного замка), схему прямо сейчас обновляет другая копия
/// приложения (замок миграций занят), сетевая папка не отвечает. <see cref="Reason"/> — короткая
/// фраза для статуса под именем базы («недоступна (…)»), она же уходит в журнал. База остается
/// под ретраями монитора: причина временная, повтор через минуту обычно проходит.
/// </summary>
public sealed class MountBlockedException(string reason, Exception? inner = null) : Exception(reason, inner)
{
    /// <summary>Причина без обвязки — для статуса «недоступна (причина)».</summary>
    public string Reason { get; } = reason;

    public const string WalHeldElsewhere   = "файл в режиме WAL, его держит другой компьютер";
    public const string MigrationElsewhere = "схему базы обновляет другой компьютер";
    public const string ShareNotResponding = "папка не отвечает";
}
