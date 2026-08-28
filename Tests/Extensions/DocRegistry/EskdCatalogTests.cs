using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>
/// Встроенный справочник ЕСКД: состав файла, починка его дефектов и ходьба по дереву.
///
/// Главный инвариант здесь — ПОРЯДОК. Файл готовится сортировкой кода как строки, и от этого
/// зависит все остальное: предок обязан стоять перед потомками (иначе не найти пропущенный узел),
/// а коды — идти по возрастанию (иначе врет бинарный поиск). На сломанном файле выдача была бы
/// не пустой, а молча неполной, поэтому порядок проверяется отдельно и целиком.
/// </summary>
public sealed class EskdCatalogTests
{
    // ── Состав файла ──────────────────────────────────────────────────────────

    /// <summary>Состав прибит: справочник — часть поставки, и его подмена должна заметиться.
    /// 78 480 строк исходных данных минус две склеенные плюс три достроенные — см. соседние
    /// тесты, каждое из этих чисел там названо своим именем.</summary>
    [Fact]
    public void Catalog_has_all_five_levels()
    {
        Assert.Equal(78_481, EskdCatalog.Count);

        var byLevel = EskdCatalog.All.GroupBy(e => e.Code.Length)
                                     .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(43,     byLevel[2]);   // классы
        Assert.Equal(290,    byLevel[3]);   // подклассы
        Assert.Equal(1_722,  byLevel[4]);   // группы
        Assert.Equal(10_240, byLevel[5]);   // подгруппы
        Assert.Equal(66_186, byLevel[6]);   // виды
    }

    /// <summary>
    /// Коды идут по возрастанию КАК СТРОКИ — а это и есть обход дерева сверху вниз:
    /// 38, 380, 3800, 38000, 380000, … 381. Сравнение именно строковое: по числам «380» шло бы
    /// после «38000», и дерево рассыпалось бы.
    /// </summary>
    [Fact]
    public void Codes_are_ordered_as_a_tree()
    {
        var previous = "";
        foreach (var entry in EskdCatalog.All)
        {
            Assert.True(string.CompareOrdinal(entry.Code, previous) >= 0,
                        $"Код {entry.Code} идет после {previous} — порядок сломан");
            previous = entry.Code;
        }
    }

    [Fact]
    public void Every_code_is_two_to_six_digits() =>
        Assert.All(EskdCatalog.All, e =>
        {
            Assert.InRange(e.Code.Length, EskdCatalog.RootLength, EskdCatalog.CodeLength);
            Assert.All(e.Code, c => Assert.True(char.IsAsciiDigit(c)));
        });

    /// <summary>Классы — то, с чего открывается окно.</summary>
    [Fact]
    public void Roots_are_the_classes()
    {
        Assert.Equal(43, EskdCatalog.Roots.Count);
        Assert.All(EskdCatalog.Roots, e => Assert.Equal(EskdCatalog.RootLength, e.Code.Length));

        // Те же объекты, что и в общем списке: строки нигде не пересоздаются.
        Assert.Same(EskdCatalog.All.First(), EskdCatalog.Roots.First());
    }

    /// <summary>Готовые списки, а не пересборка на каждое обращение.</summary>
    [Fact]
    public void Ready_lists_are_the_same_every_time()
    {
        Assert.Same(EskdCatalog.All,   EskdCatalog.All);
        Assert.Same(EskdCatalog.Roots, EskdCatalog.Roots);
    }

    /// <summary>В данных справочника второго написания буквы «е» нет — файл готовится без него.</summary>
    [Fact]
    public void Catalog_names_use_one_spelling() =>
        Assert.All(EskdCatalog.All, e => Assert.DoesNotContain('ё', e.Name.ToLowerInvariant()));

    // ── Починка дефектов исходных данных ──────────────────────────────────────

    /// <summary>
    /// Условие, ради которого починка и делается: у узла с детьми код обязан быть один. Будь их
    /// два, было бы непонятно, к какому из них дети относятся, и одна из веток пропала бы.
    /// </summary>
    [Fact]
    public void A_code_with_children_occurs_once()
    {
        var twice = EskdCatalog.All.Where(e => !e.IsLeaf)
                                   .GroupBy(e => e.Code)
                                   .Where(g => g.Count() > 1)
                                   .Select(g => g.Key)
                                   .ToList();

        Assert.Empty(twice);
    }

    /// <summary>Наименование, разорванное переносом при выгрузке, собрано обратно в одну строку
    /// (хвост «Кантования» уехал в исходнике в конец блока).</summary>
    [Fact]
    public void Wrapped_name_is_glued_back()
    {
        var node = EskdCatalog.Node("4814");

        Assert.NotNull(node);
        Assert.StartsWith("Опрокидыватели", node.Name, StringComparison.Ordinal);
        Assert.EndsWith("кантования", node.Name, StringComparison.Ordinal);
    }

    /// <summary>Двадцать шесть шестизначных кодов остались в исходнике без пятизначного родителя.
    /// Родитель достраивается — иначе до этих кодов не добраться ни щелчком, ни вводом.</summary>
    [Theory]
    [InlineData("30458")]
    [InlineData("30754")]
    [InlineData("75045")]
    public void Missing_node_is_built(string code)
    {
        var node = EskdCatalog.Node(code);

        Assert.NotNull(node);
        Assert.Equal("", node.Name);       // наименования в справочнике нет
        Assert.Equal("—", node.Title);     // в списке на его месте прочерк
        Assert.NotEmpty(EskdCatalog.Children(code));
    }

    /// <summary>
    /// Дерево целое: у каждой строки на месте ВСЯ цепочка предков. Это и значит, что до любого
    /// кода можно дощелкать от класса.
    /// </summary>
    [Fact]
    public void Every_code_is_reachable_from_its_class()
    {
        var known = EskdCatalog.All.Select(e => e.Code).ToHashSet(StringComparer.Ordinal);

        var orphans = EskdCatalog.All
            .SelectMany(e => Enumerable.Range(EskdCatalog.RootLength, e.Code.Length - EskdCatalog.RootLength)
                                       .Select(length => (Child: e.Code, Parent: e.Code[..length])))
            .Where(pair => !known.Contains(pair.Parent))
            .ToList();

        Assert.Empty(orphans);
    }

    /// <summary>Пять шестизначных кодов задвоены с разными наименованиями. Детей у них нет,
    /// поэтому склеивать нечего: обе строки остаются в списке, выбор между ними за человеком.</summary>
    [Fact]
    public void Duplicated_full_code_keeps_both_rows()
    {
        var kids = EskdCatalog.Children("33764");

        Assert.Equal(["Браслеты", "Броши"],
                     kids.Where(e => e.Code == "337641").Select(e => e.Name));
    }

    // ── Узел, цепочка, дети ───────────────────────────────────────────────────

    [Fact]
    public void Known_code_is_found()
    {
        Assert.Equal("По двум и более видам норм", EskdCatalog.Node("040000")?.Name);
        Assert.Equal("Двигатели (кроме электрических)", EskdCatalog.Node("38")?.Name);
    }

    [Fact]
    public void Missing_code_is_not_found() => Assert.Null(EskdCatalog.Node("999999"));

    /// <summary><c>Find</c> отвечает только за код обозначения — шестизначный. Промежуточный
    /// уровень в обозначение не ставится, и отдавать его отсюда нельзя.</summary>
    [Theory]
    [InlineData("301121", true)]
    [InlineData("38",     false)]
    [InlineData("0400",   false)]
    [InlineData("999999", false)]
    public void Find_answers_only_for_a_full_code(string code, bool found) =>
        Assert.Equal(found, EskdCatalog.Find(code) is not null);

    [Theory]
    [InlineData("301121", true)]
    [InlineData("04000",  false)]   // пять цифр — неполный код
    [InlineData("3011211", false)]  // семь цифр
    [InlineData("30112a", false)]
    [InlineData("",       false)]
    [InlineData(null,     false)]
    public void Full_code_is_exactly_six_digits(string? code, bool expected) =>
        Assert.Equal(expected, EskdCatalog.IsFullCode(code));

    [Fact]
    public void Chain_runs_from_the_class_down() =>
        Assert.Equal(["33", "330", "3300", "33000", "330008"],
                     EskdCatalog.Chain("330008").Select(e => e.Code));

    [Fact]
    public void Chain_of_an_unknown_code_is_empty() => Assert.Empty(EskdCatalog.Chain("999999"));

    [Fact]
    public void Children_are_the_next_level_down() =>
        Assert.Equal(["380", "381", "382", "384", "385", "387"],
                     EskdCatalog.Children("38").Select(e => e.Code));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Children_of_nothing_are_the_classes(string? code) =>
        Assert.Same(EskdCatalog.Roots, EskdCatalog.Children(code));

    /// <summary>Шестизначный код — конец ветки: раскрывать нечего.</summary>
    [Fact]
    public void A_full_code_has_no_children() => Assert.Empty(EskdCatalog.Children("330008"));

    // ── Что показать: ветка ───────────────────────────────────────────────────

    [Fact]
    public void Empty_query_shows_the_classes()
    {
        var view = EskdCatalog.View("");

        Assert.Equal(EskdViewKind.Tree, view.Kind);
        Assert.Null(view.Node);
        Assert.Equal(EskdCatalog.Roots, view.Rows);   // цепочки предков нет — только классы
        Assert.Equal(EskdCatalog.Roots.Count, view.ChildCount);
    }

    /// <summary>Ровно то, что описано в задании: цепочка предков, узел и его дети — и больше
    /// в списке ничего.</summary>
    [Fact]
    public void Digits_show_the_chain_and_the_children()
    {
        var view = EskdCatalog.View("380");

        Assert.Equal(["38", "380", "3800", "3801", "3802", "3804", "3805", "3807"],
                     view.Rows.Select(e => e.Code));
        Assert.Equal("380", view.Node?.Code);
        Assert.Equal(6, view.ChildCount);
        Assert.True(view.Exact);
    }

    /// <summary>У класса цепочка — он сам.</summary>
    [Fact]
    public void A_class_shows_itself_and_its_subclasses()
    {
        var view = EskdCatalog.View("38");

        Assert.Equal("38", view.Rows[0].Code);
        Assert.Equal(6, view.ChildCount);
        Assert.Equal(7, view.Rows.Count);
    }

    /// <summary>Введен код целиком — видна вся дорога до него сверху донизу.</summary>
    [Fact]
    public void A_full_code_shows_the_whole_chain()
    {
        var view = EskdCatalog.View("330008");

        Assert.Equal(["33", "330", "3300", "33000", "330008"], view.Rows.Select(e => e.Code));
        Assert.Equal(0, view.ChildCount);
        Assert.True(view.Exact);
    }

    /// <summary>Одна цифра — узла такого нет, зато классы просеиваются по набранному.</summary>
    [Fact]
    public void One_digit_narrows_the_classes()
    {
        var view = EskdCatalog.View("3");

        Assert.Null(view.Node);
        Assert.Equal(["30", "31", "32", "33", "38"], view.Rows.Select(e => e.Code));
    }

    /// <summary>
    /// В классификаторе есть пропуски: 3803 не существует. Вместо пустоты открывается ближайшая
    /// живая ветка — человек попал куда надо, ошибся только последней цифрой. Что кода нет,
    /// говорит <c>Exact</c>: по нему окно и пишет предупреждение.
    /// </summary>
    [Fact]
    public void A_missing_code_falls_back_to_the_nearest_branch()
    {
        var view = EskdCatalog.View("3803");

        Assert.False(view.Exact);
        Assert.Equal("380", view.Node?.Code);
        Assert.Equal(6, view.ChildCount);
    }

    [Fact]
    public void More_digits_than_a_code_still_show_the_branch()
    {
        var view = EskdCatalog.View("3300081");

        Assert.False(view.Exact);
        Assert.Equal("330008", view.Node?.Code);
        Assert.Equal(["33", "330", "3300", "33000", "330008"], view.Rows.Select(e => e.Code));
    }

    /// <summary>Строки ветки — ТЕ ЖЕ объекты, что в справочнике: список не пересоздает их на
    /// каждый шаг, а выделение переживает переход.</summary>
    [Fact]
    public void Branch_rows_are_the_same_objects() =>
        Assert.Same(EskdCatalog.Node("38"), EskdCatalog.View("380").Rows[0]);

    // ── Что показать: поиск по наименованию ───────────────────────────────────

    [Fact]
    public void A_word_gives_a_flat_list()
    {
        var view = EskdCatalog.View("трехступенчатые");

        Assert.Equal(EskdViewKind.Names, view.Kind);
        Assert.Null(view.Node);
        Assert.True(view.Total > 0);
        Assert.All(view.Rows, e => Assert.Contains("рехступенчатые", e.Name.ToLowerInvariant(),
                                                   StringComparison.Ordinal));
    }

    /// <summary>Ищется по всем уровням, а не только по шестизначным: словом из наименования
    /// класса или группы удобно попадать в нужное место дерева.</summary>
    [Fact]
    public void A_word_reaches_the_intermediate_levels()
    {
        var view = EskdCatalog.View("кроме электрических");

        Assert.Contains(view.Rows, e => e.Code == "38");
    }

    /// <summary>
    /// Данные справочника готовятся в одном написании буквы «е», а человек может набрать второе —
    /// свертка приводит запрос к тому же виду. Без нее такой запрос не нашел бы ничего.
    /// </summary>
    [Fact]
    public void Both_spellings_find_the_same_rows() =>
        Assert.Equal(EskdCatalog.View("трехступенчатые").Total,
                     EskdCatalog.View("трёхступенчатые").Total);

    [Fact]
    public void Name_search_ignores_case() =>
        Assert.Equal(EskdCatalog.View("трехступенчатые").Total,
                     EskdCatalog.View("ТРЕХСТУПЕНЧАТЫЕ").Total);

    /// <summary>Показ ограничен, но общее число нашедшегося видно — окно пишет его подсказкой.</summary>
    [Fact]
    public void A_long_result_is_truncated_but_counted()
    {
        var view = EskdCatalog.View("для", limit: 10);

        Assert.Equal(10, view.Rows.Count);
        Assert.True(view.Total > 10);
        Assert.True(view.Truncated);
    }

    [Fact]
    public void A_short_result_is_not_truncated() =>
        Assert.False(EskdCatalog.View("трехступенчатые").Truncated);

    [Fact]
    public void Nothing_found_is_an_empty_list()
    {
        var view = EskdCatalog.View("такогонаименованиянет");

        Assert.Empty(view.Rows);
        Assert.Equal(0, view.Total);
    }

    // ── Вид строки в списке ───────────────────────────────────────────────────

    /// <summary>Уровень показан точками до шести знаков — как в исходной таблице. Точки заодно
    /// и признак: есть они — строка раскрывается, нет — ее можно зафиксировать.</summary>
    [Theory]
    [InlineData("38",     "....", false)]
    [InlineData("380",    "...",  false)]
    [InlineData("3800",   "..",   false)]
    [InlineData("38000",  ".",    false)]
    [InlineData("380008", "",     true)]
    public void Dots_pad_the_code_to_six_signs(string code, string dots, bool leaf)
    {
        var entry = new EskdEntry(code, "неважно");

        Assert.Equal(dots, entry.Dots);
        Assert.Equal(leaf, entry.IsLeaf);
        Assert.Equal(EskdCatalog.CodeLength, code.Length + dots.Length);
    }

    [Fact]
    public void An_empty_name_shows_a_dash() => Assert.Equal("—", new EskdEntry("30458", "").Title);

    [Fact]
    public void A_level_has_a_name() =>
        Assert.Equal(["классов", "подклассов", "групп", "подгрупп", "видов"],
                     Enumerable.Range(2, 5).Select(EskdCatalog.LevelName));
}
