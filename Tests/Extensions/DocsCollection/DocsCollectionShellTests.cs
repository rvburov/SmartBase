using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Extensions.DocsCollection;
using SmartBase.Utils;

namespace SmartBase.Tests.Extensions.DocsCollection;

/// <summary>
/// Каркас коллекции — второй шаг расширения: коллекция, подпись системной колонки и три
/// пользовательских поля. Делается на ОБЩЕМ репозитории в UI-потоке, поэтому здесь же
/// проверяется точка отказа по правам: расширение не заводит своего права и живет под
/// <see cref="PermAction.CreateCollection"/>.
///
/// Состав и порядок колонок — часть договоренности с пользователем: он увидит именно такую
/// таблицу, и молчаливая смена набора полей ломает уже созданные коллекции.
/// </summary>
public sealed class DocsCollectionShellTests
{
    [Fact]
    public void Shell_has_the_three_user_fields()
    {
        using var db = new TempDatabase();

        var shell  = DocsCollectionBuilder.CreateShell(db.Constructor, "Документы");
        var fields = db.Constructor.GetFields(shell.CollectionId);

        Assert.Equal(3, fields.Count);
        Assert.Equal("text", fields.Single(f => f.Id == shell.FolderFieldId).FieldType);
        Assert.Equal("text", fields.Single(f => f.Id == shell.FormatFieldId).FieldType);
        Assert.Equal("file", fields.Single(f => f.Id == shell.FileFieldId).FieldType);

        Assert.Equal(DocsCollectionBuilder.FolderFieldName, fields.Single(f => f.Id == shell.FolderFieldId).Name);
        Assert.Equal(DocsCollectionBuilder.FormatFieldName, fields.Single(f => f.Id == shell.FormatFieldId).Name);
        Assert.Equal(DocsCollectionBuilder.FileFieldName,   fields.Single(f => f.Id == shell.FileFieldId).Name);
    }

    [Fact]
    public void Title_system_column_is_renamed()
    {
        using var db = new TempDatabase();

        var shell = DocsCollectionBuilder.CreateShell(db.Constructor, "Документы");

        var names = db.Constructor.GetSystemFieldNames(shell.CollectionId);
        Assert.Equal(DocsCollectionBuilder.TitleColumnName, names["__title__"]);
    }

    /// <summary>Поле «файл» заводится БЕЗ правила авто-привязки: правило умеет лишь одну папку
    /// без подпапок и один формат, чего не хватает при рекурсивном обходе смешанных форматов —
    /// пути расширение проставляет само.</summary>
    [Fact]
    public void File_field_has_no_auto_link_rule()
    {
        using var db = new TempDatabase();

        var shell = DocsCollectionBuilder.CreateShell(db.Constructor, "Документы");
        var file  = db.Constructor.GetFields(shell.CollectionId).Single(f => f.Id == shell.FileFieldId);

        // Конфиг остается дефолтным «{}» — правило из него не разбирается, значит поле
        // в ручном режиме и путь пишет расширение.
        Assert.Null(FileAutoRule.FromConfig(file.ConfigJson));
    }

    [Fact]
    public void Column_order_is_title_folder_format_file()
    {
        using var db = new TempDatabase();

        var shell = DocsCollectionBuilder.CreateShell(db.Constructor, "Документы");
        var order = db.Constructor.GetColumnOrder(shell.CollectionId);

        Assert.True(order["__title__"] < order[shell.FolderFieldId.ToString()]);
        Assert.True(order[shell.FolderFieldId.ToString()] < order[shell.FormatFieldId.ToString()]);
        Assert.True(order[shell.FormatFieldId.ToString()] < order[shell.FileFieldId.ToString()]);
    }

    // ── Имя коллекции ─────────────────────────────────────────────────────────

    [Fact]
    public void Taken_name_gets_a_numeric_suffix()
    {
        using var db = new TempDatabase();
        DocsCollectionBuilder.CreateShell(db.Constructor, "Документы");

        var second = DocsCollectionBuilder.CreateShell(db.Constructor, "Документы");

        Assert.Equal("Документы (2)", second.CollectionName);
    }

    // Нумерация не должна застревать на « (2)»: третий обход той же папки — обычное дело.
    [Fact]
    public void Third_duplicate_gets_the_next_number()
    {
        using var db = new TempDatabase();
        DocsCollectionBuilder.CreateShell(db.Constructor, "Документы");
        DocsCollectionBuilder.CreateShell(db.Constructor, "Документы");

        var third = DocsCollectionBuilder.CreateShell(db.Constructor, "Документы");

        Assert.Equal("Документы (3)", third.CollectionName);
    }

    [Fact]
    public void Blank_name_falls_back_to_the_default()
    {
        using var db = new TempDatabase();

        var shell = DocsCollectionBuilder.CreateShell(db.Constructor, "   ");

        Assert.Equal("Документы", shell.CollectionName);
    }

    // ── Права ─────────────────────────────────────────────────────────────────

    /// <summary>Своего права расширение не заводит — создание каркаса упирается в общее
    /// «Создавать коллекции». Проверка идет настоящим путем: репозиторий сам зовет Demand,
    /// расширение только ловит исключение и показывает причину в статусе окна.</summary>
    [Fact]
    public void Shell_creation_needs_the_create_collection_right()
    {
        using var db = new TempDatabase();
        SetRights(db, PermAction.All & ~PermAction.CreateCollection);

        var ex = Assert.Throws<PermissionDeniedException>(
            () => DocsCollectionBuilder.CreateShell(db.Constructor, "Документы"));

        Assert.Equal(PermAction.CreateCollection, ex.Action);
    }

    [Fact]
    public void Shell_creation_passes_with_the_right()
    {
        using var db = new TempDatabase();
        SetRights(db, PermAction.All);

        var shell = DocsCollectionBuilder.CreateShell(db.Constructor, "Документы");

        Assert.True(shell.CollectionId > 0);
    }

    // ── Уборка ────────────────────────────────────────────────────────────────

    [Fact]
    public void Try_delete_removes_the_collection()
    {
        using var db = new TempDatabase();
        var shell = DocsCollectionBuilder.CreateShell(db.Constructor, "Документы");

        DocsCollectionBuilder.TryDelete(db.Constructor, shell.CollectionId);

        Assert.DoesNotContain(db.Constructor.GetAllCollections(), c => c.Id == shell.CollectionId);
    }

    // Уборка вызывается в catch — ее собственная ошибка не должна перебивать исходную причину.
    [Fact]
    public void Try_delete_of_a_missing_collection_is_silent()
    {
        using var db = new TempDatabase();

        DocsCollectionBuilder.TryDelete(db.Constructor, collectionId: 999_999);
    }

    // Понижает текущего пользователя до допущенного не-админа с заданной маской на всю базу
    // (тот же прием, что в матрице прав ядра: маски пишет только администратор).
    private static void SetRights(TempDatabase db, PermAction mask)
    {
        var me = db.Context.DbUsers.First(u => u.UserKey == CurrentUser.Key);
        me.IsAdmin = true;
        db.Context.SaveChanges();
        db.Permissions.Reload(notify: false);
        db.Permissions.SetMask(db.Permissions.CurrentUserId, null, (int)mask);

        me.IsAdmin    = false;   // теперь маска действует: администратору можно все
        me.IsApproved = true;
        db.Context.SaveChanges();
        db.Permissions.Reload(notify: false);
    }
}
