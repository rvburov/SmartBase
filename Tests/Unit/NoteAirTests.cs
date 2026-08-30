using SmartBase.Core.Notes;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Воздух над первой и под последней строкой заметки. Проверять тут нечего, кроме краев, — но
/// именно края и ломаются: у пустой заметки начало и конец совпадают, и наивное «либо сверху, либо
/// снизу» оставило бы ее без воздуха с одной стороны.
/// </summary>
public sealed class NoteAirTests
{
    private const double Air  = 16;
    private const double Line = 20;   // высота пустой строки в конце файла

    // ── Точки интереса ──────────────────────────────────────────────────────────

    [Fact]
    public void Interest_Starts_At_The_Very_Beginning() =>
        Assert.Equal(0, NoteAir.InterestFrom(0, 100));

    [Fact]
    public void Interest_After_The_Beginning_Points_At_The_End() =>
        Assert.Equal(100, NoteAir.InterestFrom(1, 100));

    [Fact]
    public void Interest_At_The_End_Points_At_The_End() =>
        Assert.Equal(100, NoteAir.InterestFrom(100, 100));

    [Fact]
    public void Interest_Past_The_End_Is_Gone() =>
        Assert.Equal(-1, NoteAir.InterestFrom(101, 100));

    // ── Сколько воздуха и где ───────────────────────────────────────────────────

    [Fact]
    public void First_Line_Gets_Air_Above_Only()
    {
        var (top, bottom) = NoteAir.At(0, 100, Air, 0);
        Assert.Equal(Air, top);
        Assert.Equal(0,   bottom);
    }

    [Fact]
    public void Last_Line_Gets_Air_Below_Only()
    {
        var (top, bottom) = NoteAir.At(100, 100, Air, 0);
        Assert.Equal(0,   top);
        Assert.Equal(Air, bottom);
    }

    [Fact]
    public void Middle_Of_The_Note_Gets_No_Air()
    {
        var (top, bottom) = NoteAir.At(50, 100, Air, 0);
        Assert.Equal(0, top);
        Assert.Equal(0, bottom);
    }

    [Fact]
    public void Empty_Note_Gets_Air_On_Both_Sides_At_Once()
    {
        var (top, bottom) = NoteAir.At(0, 0, Air, 0);
        Assert.Equal(Air, top);
        Assert.Equal(Air, bottom);
    }

    // ── Пустая строка в конце файла ─────────────────────────────────────────────

    /// <summary>
    /// Главное свойство: сколько воздуха видно сверху, столько же видно и снизу. Снизу к отступу
    /// добавляется сама пустая строка, поэтому сверху ее высота приписывается к отступу.
    /// </summary>
    [Fact]
    public void Trailing_Blank_Line_Is_Mirrored_At_The_Top()
    {
        var (top, _)      = NoteAir.At(0,   100, Air, Line);
        var (_,   bottom) = NoteAir.At(100, 100, Air, Line);

        Assert.Equal(Air + Line, top);
        Assert.Equal(Air,        bottom);
        Assert.Equal(top, bottom + Line);   // на экране просветы равны
    }

    [Fact]
    public void Without_A_Trailing_Blank_Line_Both_Ends_Keep_The_Plain_Air()
    {
        var (top, _)      = NoteAir.At(0,   100, Air, 0);
        var (_,   bottom) = NoteAir.At(100, 100, Air, 0);

        Assert.Equal(top, bottom);
    }

    [Fact]
    public void Trailing_Blank_Line_Does_Not_Reach_The_Middle()
    {
        var (top, bottom) = NoteAir.At(50, 100, Air, Line);
        Assert.Equal(0, top);
        Assert.Equal(0, bottom);
    }

    [Fact]
    public void Empty_Note_Has_A_Single_Interesting_Offset()
    {
        Assert.Equal(0,  NoteAir.InterestFrom(0, 0));
        Assert.Equal(-1, NoteAir.InterestFrom(1, 0));
    }
}
