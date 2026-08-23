using SmartBase.Database.Repositories;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Описатель смонтированной БД для диалогов создания/изменения: id, имя и репозиторий
/// конструктора этой БД. Через него комбобокс «База данных» выбирает целевую БД, а
/// «Расположение» перестраивается под ее группы.
/// </summary>
public sealed record DbOption(int Id, string Name, ConstructorRepository Repo);
