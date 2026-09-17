using SmartBase.ViewModels.ConstructorDb;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Карточка «Название» в панели записи (<see cref="RecordPageViewModel"/>): стоит на месте
/// колонки «Название» в таблице, подписана ее именем, а набранное в любом из трех мест правки
/// (шапка, карточка, нижний блок) сохраняется одним правилом — без крайних пробелов, пустое как
/// «Без названия».
/// </summary>
public sealed class RecordPageTitleTests
{
    private static List<string> Order(RecordPageViewModel vm) =>
        vm.OrderedItems.Select(i => i switch
        {
            TitleItem    => "title",
            FieldItem fi => fi.Row.Field.Name,
            _            => "?",
        }).ToList();

    [Fact]
    public void Title_card_stands_where_the_title_column_does()
    {
        using var db = new TempDatabase();
        var repo  = db.Constructor;
        var col   = SampleData.AddCollection(repo, "События").Id!.Value;
        var first = SampleData.AddField(repo, col, "Проверка", position: 10).Id!.Value;
        SampleData.AddField(repo, col, "Итог", position: 20);
        var rid   = SampleData.AddRecord(repo, col, "Событие").Id!.Value;

        var vm = new RecordPageViewModel(repo, rid, col);
        Assert.Equal(new[] { "title", "Проверка", "Итог" }, Order(vm));
        Assert.Equal("Название", vm.TitleLabel);

        // Колонку «Название» сдвинули и переименовали в таблице — карточка следом.
        repo.SaveColumnOrder(col, [first.ToString(), TableRowVm.TitleKey]);
        repo.SetSystemFieldName(col, TableRowVm.TitleKey, "Тема", "Название");

        vm = new RecordPageViewModel(repo, rid, col);
        Assert.Equal(new[] { "Проверка", "title", "Итог" }, Order(vm));
        Assert.Equal("Тема", vm.TitleLabel);
    }

    [Fact]
    public void Typed_title_is_trimmed_and_empty_is_stored_as_placeholder()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "События").Id!.Value;
        var rid  = SampleData.AddRecord(repo, col, "Событие").Id!.Value;
        var vm   = new RecordPageViewModel(repo, rid, col);

        vm.SaveTitleInput("  Длинное название события  ");
        Assert.Equal("Длинное название события", repo.GetRecord(rid)!.Title);
        Assert.Equal("Длинное название события", vm.EditableTitle);

        vm.SaveTitleInput("   ");
        Assert.Equal("Без названия", repo.GetRecord(rid)!.Title);
        Assert.Equal("", vm.EditableTitle);   // полям ввода «Без названия» — плейсхолдер, не текст

        // Многострочное (импорт) хранится как есть, без крайних пробелов.
        vm.SaveTitleInput("Первая\nВторая\n");
        Assert.Equal("Первая\nВторая", repo.GetRecord(rid)!.Title);
    }
}
