using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Сторож реестра действий: id уникальны и стабильны (id — суффикс ключа в app.db, его
/// переименование сбрасывает пользовательскую привязку), стандартные жесты разбираются, и
/// главное — стандарты не конфликтуют между собой в пересекающихся зонах. Единственное
/// законное исключение — тройка Delete-действий таблицы, которую разводит состояние
/// выделения, как и было до настраиваемых сочетаний.
/// </summary>
public sealed class ShortcutActionsTests
{
    [Fact]
    public void Ids_are_unique_and_shaped_as_block_dot_name()
    {
        var ids = ShortcutActions.All.Select(a => a.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());

        foreach (var id in ids)
        {
            var dot = id.IndexOf('.');
            Assert.True(dot > 0 && dot < id.Length - 1, $"id без блока: {id}");
            Assert.DoesNotContain(' ', id);
        }
    }

    [Fact]
    public void Every_rebindable_action_has_a_default_and_reference_rows_have_none()
    {
        foreach (var action in ShortcutActions.All)
        {
            if (action.Rebindable)
            {
                Assert.NotEmpty(action.Defaults);
                Assert.NotEqual(ShortcutContext.Reference, action.Context);
            }
            else
            {
                Assert.Empty(action.Defaults);
                Assert.False(string.IsNullOrWhiteSpace(action.FixedLabel), $"справочная строка без подписи: {action.Id}");
            }
        }
    }

    [Fact]
    public void Defaults_are_already_canonical()
    {
        foreach (var action in ShortcutActions.All)
            foreach (var gesture in action.Defaults)
                Assert.Equal(gesture, ShortcutGesture.Parse(gesture.Canonical));
    }

    [Fact]
    public void Defaults_do_not_collide_inside_overlapping_contexts()
    {
        var pairs = ShortcutActions.All.Where(a => a.Rebindable).ToList();

        foreach (var a in pairs)
            foreach (var b in pairs)
            {
                if (ReferenceEquals(a, b)) continue;
                if (!ShortcutActions.ContextsOverlap(a.Context, b.Context)) continue;
                if (ShortcutActions.AllowSameGesture(a.Id, b.Id)) continue;

                var shared = a.Defaults.Intersect(b.Defaults).ToList();
                Assert.True(shared.Count == 0,
                    $"стандарты пересеклись: {a.Id} и {b.Id} — {string.Join(", ", shared.Select(g => g.Canonical))}");
            }
    }

    [Fact]
    public void The_delete_trio_of_the_table_is_the_only_allowed_overlap()
    {
        Assert.True(ShortcutActions.AllowSameGesture("table.clearCells", "table.deleteRecords"));
        Assert.True(ShortcutActions.AllowSameGesture("table.deleteColumns", "table.clearCells"));
        Assert.False(ShortcutActions.AllowSameGesture("table.copy", "table.paste"));
        Assert.False(ShortcutActions.AllowSameGesture("table.clearCells", "sidebar.delete"));
    }

    [Fact]
    public void Local_contexts_do_not_overlap_but_global_touches_everything()
    {
        // Delete значит разное в таблице, календаре и боковой панели — это не конфликт.
        Assert.False(ShortcutActions.ContextsOverlap(ShortcutContext.Table, ShortcutContext.Calendar));
        Assert.False(ShortcutActions.ContextsOverlap(ShortcutContext.Sidebar, ShortcutContext.Notes));
        Assert.True(ShortcutActions.ContextsOverlap(ShortcutContext.Table, ShortcutContext.Table));
        Assert.True(ShortcutActions.ContextsOverlap(ShortcutContext.Global, ShortcutContext.Notes));
        Assert.True(ShortcutActions.ContextsOverlap(ShortcutContext.Notes, ShortcutContext.Global));
        // Справочные строки не участвуют в конфликтах вовсе.
        Assert.False(ShortcutActions.ContextsOverlap(ShortcutContext.Reference, ShortcutContext.Global));
    }

    [Fact]
    public void Titles_are_russian_without_the_yo_letter()
    {
        foreach (var action in ShortcutActions.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(action.Title), $"без названия: {action.Id}");
            Assert.DoesNotContain('ё', action.Title + (action.FixedLabel ?? ""));
            Assert.DoesNotContain('Ё', action.Title + (action.FixedLabel ?? ""));
        }

        foreach (var block in Enum.GetValues<ShortcutBlock>())
            Assert.False(string.IsNullOrWhiteSpace(ShortcutActions.BlockTitle(block)));
    }

    [Fact]
    public void A_row_is_named_block_colon_action()
    {
        var copy = ShortcutActions.Find("table.copy")!;
        Assert.Equal("Таблица: Копировать значения ячеек", ShortcutActions.FullTitle(copy));
    }

    [Fact]
    public void Find_returns_the_action_or_null_for_an_unknown_id()
    {
        Assert.Equal("table.copy", ShortcutActions.Find("table.copy")?.Id);
        Assert.Null(ShortcutActions.Find("table.thereIsNoSuchAction"));
    }

    [Fact]
    public void The_new_actions_the_program_did_not_have_are_present()
    {
        string[] added =
        [
            "table.createRecord", "table.duplicateRecord",
            "sidebar.createCollection", "sidebar.duplicateCollection", "sidebar.createGroup",
            "app.zoomIn", "app.zoomOut", "app.zoomReset",
        ];
        foreach (var id in added)
            Assert.True(ShortcutActions.Find(id) is { Rebindable: true }, $"нет действия {id}");
    }
}
