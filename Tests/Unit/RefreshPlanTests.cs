using SmartBase.Views;
using static SmartBase.Views.RefreshPlan;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Правило «что перечитать после своего коммита» (<see cref="RefreshPlan"/>): автор правки не
/// перечитывается, устаревшие части перечитываются, ввод не затирается, скрытые разделы не
/// трогаются, таблица отличает структуру от строк.
/// </summary>
public sealed class RefreshPlanTests
{
    private const long Commit = 50;
    private const long Fresh  = 100;   // читали ПОСЛЕ коммита
    private const long Old    = 10;    // читали ДО коммита

    // Экран, где все части свежие: любой шаг в плане — только от явно состаренной отметки.
    private static Screen AllFresh(string module = "constructor", bool structural = false) => new(
        CommitStamp:             Commit,
        Structural:              structural,
        ActiveModule:            module,
        CalendarSidebarStamp:    Fresh,
        TasksSidebarStamp:       Fresh,
        ConstructorSidebarStamp: Fresh,
        CalendarStamp:           Fresh,
        TasksStamp:              Fresh,
        ConstructorShowsDb:      true,
        ConstructorStamp:        Fresh,
        CellEditing:             false,
        RecordPanelShowsDb:      true,
        RecordPanelStamp:        Fresh,
        RecordPanelFocused:      false);

    [Fact]
    public void Stale_means_read_before_commit_and_no_commit_is_never_stale()
    {
        Assert.True(IsStale(Old, Commit));
        Assert.False(IsStale(Commit, Commit));
        Assert.False(IsStale(Fresh, Commit));
        Assert.False(IsStale(0, 0));       // раздел еще ничего не читал, коммитов не было
        Assert.True(IsStale(0, Commit));   // раздел ничего не читал, а коммит был
    }

    [Fact]
    public void Nothing_stale_means_nothing_to_do()
    {
        Assert.Equal(Steps.None, After(AllFresh()));
        Assert.Equal(Steps.None, After(AllFresh(structural: true)));
    }

    [Fact]
    public void Author_of_the_edit_is_skipped_and_stale_parts_are_refreshed()
    {
        // Правка ячейки: таблица обновила себя (свежая), панель показывала ту же базу и устарела.
        var steps = After(AllFresh() with { RecordPanelStamp = Old });

        Assert.Equal(Steps.RecordPanel, steps);
    }

    [Fact]
    public void Structural_commit_rebuilds_only_stale_sidebars_and_the_whole_table()
    {
        var steps = After(AllFresh(structural: true) with
        {
            CalendarSidebarStamp = Old,
            TasksSidebarStamp    = Old,
            ConstructorStamp     = Old,
        });

        Assert.True(steps.Has(Steps.CalendarSidebar));
        Assert.True(steps.Has(Steps.TasksSidebar));
        Assert.False(steps.Has(Steps.ConstructorSidebar));   // свежий — свой раздел уже перестроил
        Assert.True(steps.Has(Steps.ConstructorFull));
        Assert.False(steps.Has(Steps.ConstructorRows));
    }

    [Fact]
    public void Record_commit_refreshes_rows_in_place_and_leaves_sidebars_alone()
    {
        var steps = After(AllFresh() with
        {
            CalendarSidebarStamp    = Old,
            TasksSidebarStamp       = Old,
            ConstructorSidebarStamp = Old,
            ConstructorStamp        = Old,
        });

        Assert.Equal(Steps.ConstructorRows, steps);
    }

    [Fact]
    public void Only_the_visible_aggregate_section_is_reread()
    {
        var stale = AllFresh() with { CalendarStamp = Old, TasksStamp = Old };

        Assert.Equal(Steps.Calendar, After(stale with { ActiveModule = "calendar" }));
        Assert.Equal(Steps.Tasks,    After(stale with { ActiveModule = "tasks" }));
        Assert.Equal(Steps.None,     After(stale with { ActiveModule = "constructor" }));
        Assert.Equal(Steps.None,     After(stale with { ActiveModule = "notes" }));
    }

    [Fact]
    public void Table_of_another_database_and_closed_panel_are_not_touched()
    {
        var steps = After(AllFresh() with
        {
            ConstructorShowsDb = false, ConstructorStamp  = Old,
            RecordPanelShowsDb = false, RecordPanelStamp  = Old,
        });

        Assert.Equal(Steps.None, steps);
    }

    [Fact]
    public void Typing_in_a_cell_defers_the_table_but_not_the_rest()
    {
        var steps = After(AllFresh(module: "calendar") with
        {
            ConstructorStamp = Old, CellEditing = true,
            RecordPanelStamp = Old,
            CalendarStamp    = Old,
        });

        Assert.True(steps.Has(Steps.Retry));
        Assert.False(steps.Has(Steps.ConstructorRows));
        Assert.False(steps.Has(Steps.ConstructorFull));
        Assert.True(steps.Has(Steps.RecordPanel));   // фокус не в панели — ее перечитать можно
        Assert.True(steps.Has(Steps.Calendar));
    }

    [Fact]
    public void Focus_in_the_record_panel_defers_the_panel_only()
    {
        var steps = After(AllFresh() with
        {
            RecordPanelStamp = Old, RecordPanelFocused = true,
            ConstructorStamp = Old,
        });

        Assert.True(steps.Has(Steps.Retry));
        Assert.False(steps.Has(Steps.RecordPanel));
        Assert.True(steps.Has(Steps.ConstructorRows));
    }

    [Fact]
    public void No_retry_when_nobody_is_typing()
    {
        var steps = After(AllFresh() with { ConstructorStamp = Old, RecordPanelStamp = Old });

        Assert.False(steps.Has(Steps.Retry));
        Assert.True(steps.Has(Steps.ConstructorRows));
        Assert.True(steps.Has(Steps.RecordPanel));
    }

    [Fact]
    public void Retry_round_after_typing_finds_the_rest_already_fresh()
    {
        // Первый заход: таблица в редакторе, календарь устарел → календарь перечитан, таблица ждет.
        // Второй заход с тем же коммитом: календарь уже свежий, редактор закрыт → только строки.
        var second = After(AllFresh(module: "calendar") with { ConstructorStamp = Old, CellEditing = false });

        Assert.Equal(Steps.ConstructorRows, second);
    }
}
