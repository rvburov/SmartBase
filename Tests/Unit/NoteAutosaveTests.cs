using SmartBase.Core.Notes;
using SmartBase.Views.Notes;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Правило, от которого зависит сохранность чужого текста: заметку, изменившуюся на диске после
/// открытия, автосохранение не имеет права перезаписать молча. Сравнение снимков — единственная
/// защита от гонки с синхронизаторами облака и вторым редактором.
/// </summary>
public sealed class NoteAutosaveTests
{
    [Fact]
    public void Untouched_File_Is_Not_A_Conflict()
    {
        var opened = new NoteSnapshot(1000, 42);

        Assert.False(NoteAutosave.ChangedOutside(opened, opened));
    }

    [Fact]
    public void Changed_Write_Time_Is_A_Conflict()
    {
        var opened = new NoteSnapshot(1000, 42);

        Assert.True(NoteAutosave.ChangedOutside(opened, new NoteSnapshot(2000, 42)));
    }

    [Fact]
    public void Changed_Length_Is_A_Conflict()
    {
        // Правка «в ту же секунду» не меняет метку времени на файловых системах с грубым
        // разрешением — длина ловит и такой случай.
        var opened = new NoteSnapshot(1000, 42);

        Assert.True(NoteAutosave.ChangedOutside(opened, new NoteSnapshot(1000, 43)));
    }

    [Fact]
    public void Vanished_File_Is_A_Conflict()
    {
        // Воссоздавать удаленный файл молча — значит спорить с тем, кто его удалил.
        Assert.True(NoteAutosave.ChangedOutside(new NoteSnapshot(1000, 42), null));
    }

    [Fact]
    public void Conflict_Copy_Keeps_The_Extension()
    {
        var name = NoteAutosave.ConflictName("Отчет.md", new DateTime(2026, 8, 30, 14, 22, 0));

        Assert.Equal("Отчет (конфликт 2026-08-30 14-22).md", name);
        Assert.True(VaultRules.IsMarkdown(name));
    }

    [Fact]
    public void Preview_Waits_Less_Than_The_Save()
    {
        // Превью догоняет набор чаще, чем идет запись на диск: перерисовать дешевле, чем писать.
        Assert.True(NoteAutosave.PreviewDebounce < NoteAutosave.Debounce);
    }
}
