using Avalonia.Controls;
using SmartBase.Database;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Выдача токена робота целиком: окно выбора имени и базы, строка реестра, пользователь-робот в
/// базе и единственный показ текста токена (docs/ROADMAP-API.md, 10.12).
///
/// <b>Почему поток вынесен.</b> Выдают токен из двух мест — «Настройки → Интеграции → Доступ
/// извне» и меню «Добавить пароль» менеджера паролей. Порядок шагов здесь не косметика: робот
/// заводится СРАЗУ вместе с токеном, а текст показывается ровно один раз и тут же копируется. Две
/// копии этого порядка разошлись бы на первой же правке — и разошлись бы как раз в том, что
/// забывать нельзя.
///
/// Какие базы предлагать, решает вызывающий: «Доступ извне» дает все допущенные, менеджер паролей
/// — только те, где человек администратор.
/// </summary>
public static class TokenIssue
{
    /// <summary>Выдать токен; истина — выдан.</summary>
    /// <param name="owner">Окно, поверх которого показывать окна потока.</param>
    /// <param name="registry">Реестр токенов.</param>
    /// <param name="databases">Базы, под которые можно выдать токен.</param>
    /// <param name="issued">Токен выдан, текст еще не показан: вызывающий перерисовывает свой
    /// список, пока окно с текстом стоит поверх.</param>
    public static async Task<bool> RunAsync(Window owner, IntegrationsRegistry registry,
                                            IReadOnlyList<DatabaseManager> databases,
                                            Action? issued = null)
    {
        if (databases.Count == 0)
        {
            await ConfirmDialog.Info("Сначала откройте базу данных: токен выдается под нее.")
                               .ShowDialog(owner);
            return false;
        }

        var made   = "";
        var target = "";

        var dialog = new TokenDialog(
            [.. databases.Select(d => (d.DbId, d.Name))],
            (name, dbId) =>
            {
                var database = databases.FirstOrDefault(d => d.DbId == dbId);
                if (database is null) return "эта база сейчас не открыта";

                var (token, secret) = registry.Issue(name, dbId, Environment.MachineName,
                                                     DateTimeOffset.Now);

                // Робот заводится СРАЗУ: токен, которому не соответствует пользователь, работать
                // не будет, и узнавать об этом из отказа первой посылки — потерянный вечер.
                database.Permissions.EnsureRobot(token.RobotKey, token.Name, DateTimeOffset.Now);

                made   = secret;
                target = database.Name;
                return null;
            });

        if (await dialog.ShowDialog<bool>(owner) != true || made.Length == 0) return false;

        issued?.Invoke();

        // Текст токена показывается ОДИН раз: хранилище, из которого секрет можно прочитать
        // обратно, рано или поздно прочитают. Отсюда и копирование сразу по закрытии.
        await ConfirmDialog.Info(
            $"Токен для базы «{target}» создан.\n\n{made}\n\n"
            + "Скопируйте его сейчас: показать второй раз программа не сможет — она хранит "
            + "только проверочный отпечаток. Потеряется — выдайте новый.",
            "Скопировать и закрыть").ShowDialog(owner);

        await (TopLevel.GetTopLevel(owner)?.Clipboard?.SetTextAsync(made) ?? Task.CompletedTask);

        return true;
    }
}
