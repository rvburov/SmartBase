using System.Text.RegularExpressions;
using SmartBase.Database;
using SmartBase.Extensions;
using SmartBase.Extensions.DocRegistry;
using SmartBase.Integrations;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>
/// Оформление ответов разметкой HTML (<see cref="ReplyMarkup"/>, решения пользователя
/// 21.10.2026): что выделено и как, что экранируется, и что слова ответов остались прежними.
/// Сами слова и порядок строк проверяют наборы действий — по тексту, который видит человек
/// (<see cref="ReplyMarkup.Text"/>).
/// </summary>
public sealed class ReplyMarkupTests
{
    private static readonly DateTimeOffset Now = new(new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Local));

    private static readonly RegisterMessageAction Action = new();

    private static ActionContext Context(TempDatabase db, int journal) =>
        new(db.Constructor, 1, journal, Now, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DocRegistryExtension.OrgCodesKey] = "АБВГ, ГМБВ",
            [DocRegistryExtension.SendersKey]  = "1001 Иванов",
        });

    private static ActionResult Run(ActionContext context, string text, string sender = "1001") =>
        Action.Run(context, new ActionInput(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RegisterMessageAction.TextField]   = text,
            [RegisterMessageAction.SenderField] = sender,
        }));

    private static string Message(string number = "АБВГ.301121.ХХХ", string name = "Кронштейн") =>
        $"Действие: Регистрация\nНомер: {number}\nНаименование: {name}\nПроект: Машина\nФИО: Иванов Иван Иванович";

    /// <summary>Инструкция — как ее получает человек.</summary>
    private static string Instruction() =>
        Run(new ActionContext(null, 1, 1, Now, new Dictionary<string, string>
            { [DocRegistryExtension.SendersKey] = "1001" }), "Инструкция").Answer;

    private static string[] Words(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    // ── Строки ошибок ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Номер: неправильный номер (Пример: АААА.000000.ХХХ, АААА.000000.001 или АААА.000000.001-ХХ)",
                "<b>Номер:</b> неправильный номер (Пример: <i>АААА.000000.ХХХ, АААА.000000.001 или АААА.000000.001-ХХ</i>)")]
    [InlineData("Номер: неизвестный код организации ЖЗИК (Можно: АБВГ, ГМБВ)",
                "<b>Номер:</b> неизвестный код организации ЖЗИК (Можно: <i>АБВГ, ГМБВ</i>)")]
    [InlineData("Номер: документ АБВГ.301121.005 уже зарегистрирован",
                "<b>Номер:</b> документ <b>АБВГ.301121.005</b> уже зарегистрирован")]
    [InlineData("Номер: нужен полный номер документа или исполнения (Пример: АБВГ.301121.001 или АБВГ.301121.001-01)",
                "<b>Номер:</b> нужен полный номер документа или исполнения (Пример: <i>АБВГ.301121.001 или АБВГ.301121.001-01</i>)")]
    [InlineData("Ожидается подтверждение: Удаление, АБВГ.301121.001-01. Подтвердите: Да или Нет. Для нового запроса сначала ответьте Нет",
                "<b>Ожидается подтверждение:</b> Удаление, <b>АБВГ.301121.001-01</b>. Подтвердите: <b>Да</b> или <b>Нет</b>. Для нового запроса сначала ответьте <b>Нет</b>")]
    [InlineData("Цвет<1> & вес: изменение этого поля не поддерживается",
                "<b>Цвет&lt;1&gt; &amp; вес:</b> изменение этого поля не поддерживается")]
    [InlineData("Примечание (первичное применение): изменение этого поля не поддерживается",
                "<b>Примечание (первичное применение):</b> изменение этого поля не поддерживается")]
    [InlineData("Прим. ред: изменение этого поля не поддерживается",
                "Прим. ред: изменение этого поля не поддерживается")]
    public void A_problem_line_has_its_field_bold_and_its_example_italic(string plain, string html)
    {
        Assert.Equal(html, ReplyMarkup.Problem(plain));
        Assert.Equal(plain, ReplyMarkup.Text(html));
    }

    [Theory]
    [InlineData("Серия изменилась после запроса подтверждения. Регистрация не выполнена. Отправьте запрос заново",
                "<b>Серия изменилась после запроса подтверждения.</b> Регистрация не выполнена. Отправьте запрос заново")]
    [InlineData("Ожидается подтверждение. Для нового запроса сначала ответьте Нет",
                "<b>Ожидается подтверждение.</b> Для нового запроса сначала ответьте <b>Нет</b>")]
    public void A_headed_refusal_has_its_first_sentence_bold(string plain, string html)
    {
        Assert.Equal(html, ReplyMarkup.Headed(plain));
        Assert.Equal(plain, ReplyMarkup.Text(html));
    }

    [Fact]
    public void Escaping_covers_all_three_signs_and_reads_back()
    {
        Assert.Equal("a &lt;b&gt; &amp;amp; c", ReplyMarkup.Escape("a <b> &amp; c"));
        Assert.Equal("a <b> &amp; c", ReplyMarkup.Text(ReplyMarkup.Escape("a <b> &amp; c")));
    }

    // ── Ответы действия ───────────────────────────────────────────────────────

    /// <summary>Ошибки сообщения — отправителю оформленными, в журнал запусков — простым текстом.</summary>
    [Fact]
    public void Message_problems_are_formatted_for_the_sender_and_plain_for_the_journal()
    {
        using var db = new TempDatabase();
        var journal = JournalStand.Create(db.Constructor);

        var result = Run(Context(db, journal), Message("ЖЗИК.301121.ХХХ").Replace("Машина", ""));

        Assert.False(result.Ok);
        Assert.Equal(
            "<b>Номер:</b> неизвестный код организации ЖЗИК (Можно: <i>АБВГ, ГМБВ</i>)\n"
            + "<b>Проект:</b> не указан проект",
            result.Reply);
        Assert.Equal("Номер: неизвестный код организации ЖЗИК (Можно: АБВГ, ГМБВ)\nПроект: не указан проект",
                     result.Explanation);
    }

    [Fact]
    public void An_unlisted_sender_gets_the_normal_registration_question()
    {
        using var db = new TempDatabase();
        var journal = JournalStand.Create(db.Constructor);

        var result = Run(Context(db, journal), Message(), sender: "9999");

        Assert.True(result.Ok, result.Explanation);
        Assert.StartsWith("<b>Заявка на регистрацию</b>\n", result.Reply, StringComparison.Ordinal);
        Assert.DoesNotContain("<", result.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Первый номер серии: заявка с номером жирным, предупреждение жирным целиком, просьба
    /// ответить и срок курсивом. После «Да» — «Зарегистрировано» с номером жирным.
    /// </summary>
    [Fact]
    public void The_first_number_question_and_the_registration_are_formatted()
    {
        using var db = new TempDatabase();
        var journal = JournalStand.Create(db.Constructor);
        var context = Context(db, journal);

        var preview = Run(context, Message());

        Assert.StartsWith("<b>Заявка на регистрацию</b>\nНомер: <b>АБВГ.301121.001</b>\nНаименование: Кронштейн\n",
                          preview.Reply, StringComparison.Ordinal);
        Assert.Contains("\n\n<b>Это первый номер в данной серии классификатора. Вы уверены, что номер верный?</b>\n\n",
                        preview.Reply, StringComparison.Ordinal);
        Assert.EndsWith("\n\nПодтвердите: <b>Да</b> или <b>Нет</b>\n<i>Подтверждение действует 10 минут</i>",
                        preview.Reply, StringComparison.Ordinal);
        Assert.Equal("Заявка на регистрацию", preview.Explanation);

        var done = Run(context, "Да");

        Assert.Equal("<b>Зарегистрировано</b>\nНомер: <b>АБВГ.301121.001</b>\nНаименование: Кронштейн\n"
                     + "Проект: Машина\nФИО: Иванов Иван Иванович", done.Reply);

        var again = Run(context, Message("АБВГ.301121.001"));
        Assert.Equal("<b>Номер:</b> документ <b>АБВГ.301121.001</b> уже зарегистрирован", again.Reply);
    }

    [Fact]
    public void An_unknown_code_warning_is_bold_as_a_whole()
    {
        using var db = new TempDatabase();
        var journal = JournalStand.Create(db.Constructor);

        var preview = Run(Context(db, journal), Message("АБВГ.000000.ХХХ"));

        Assert.Contains("\n\n<b>Код классификации не найден в доступном справочнике ЕСКД. Возможно, он относится к "
                        + "закрытому классификатору. Вы уверены, что номер указан верно?</b>\n\n",
                        preview.Reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// Значения журнала и сообщения экранируются: наименование с угловыми скобками и амперсандом
    /// доходит до человека теми же знаками, а не тегом. В журнал ложится как написано.
    /// </summary>
    [Fact]
    public void Values_are_escaped_and_reach_the_journal_as_written()
    {
        using var db = new TempDatabase();
        var journal = JournalStand.Create(db.Constructor);
        var context = Context(db, journal);
        const string name = "Кронштейн <левый> & правый";

        Run(context, Message(name: name));
        var done = Run(context, "Да");

        Assert.True(done.Ok, done.Explanation);
        Assert.Contains("Наименование: Кронштейн &lt;левый&gt; &amp; правый\n", done.Reply, StringComparison.Ordinal);
        Assert.Contains($"Наименование: {name}\n", ReplyMarkup.Text(done.Reply), StringComparison.Ordinal);
        Assert.Equal(name, Assert.Single(db.Constructor.GetRecords(journal)).Title);

        var viewed = Run(context, "Действие: Просмотр\nНомер: АБВГ.301121.001");
        Assert.Contains("Наименование: Кронштейн &lt;левый&gt; &amp; правый\n", viewed.Reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// Запрос на изменение — ровно как одобренный пример: карточка строками, номер жирным,
    /// перечень изменений со старым значением зачеркнутым и новым жирным. С переносом «&lt;br&gt;»
    /// сценария это и есть сообщение, которое увидит человек.
    /// </summary>
    [Fact]
    public void The_edit_request_matches_the_approved_example()
    {
        using var db = new TempDatabase();
        var journal = JournalStand.Create(db.Constructor);
        var shape = JournalStand.Shape(db.Constructor, journal);
        var id = DocRegistrar.Register(db.Constructor, shape,
            new RegistrationInput("Кронштейн", "АБВГ", "301121", "Иванов Иван Иванович", "Машина"),
            Now.LocalDateTime).RecordId;
        var date = db.Constructor.GetFieldValues(id)[shape.DateFieldId];
        var context = Context(db, journal);

        var preview = Run(context, "Действие: Изменение\nНомер: АБВГ.301121.001\nПроект: Станок");

        Assert.Equal(
            "<b>Подтвердите изменение документа:</b><br>Номер: <b>АБВГ.301121.001</b><br>Наименование: Кронштейн<br>"
            + $"Проект: Машина<br>ФИО: Иванов Иван Иванович<br>Дата регистрации: {date}<br>Отметка учета: —<br>"
            + "Примечание (первичное применение): —<br>Примечание (краткая характеристика): —<br><br>"
            + "<b>Изменения:</b><br>Проект: <s>Машина</s> → <b>Станок</b><br><br>"
            + "Подтвердите: <b>Да</b> или <b>Нет</b><br><i>Подтверждение действует 10 минут</i>",
            ScenarioTrigger.OnStream(3, 7, "", "<br>").WithBreaks(preview.Reply));
        Assert.Equal("Подтвердите изменение документа:", preview.Explanation);

        var done = Run(context, "Да");

        Assert.StartsWith("<b>Изменено</b>\nНомер: <b>АБВГ.301121.001</b>\nНаименование: Кронштейн\n"
                          + "Проект: <b>Станок</b>\nФИО: Иванов Иван Иванович\n",
                          done.Reply, StringComparison.Ordinal);
    }

    [Fact]
    public void Deletion_and_cancellation_have_bold_headings()
    {
        using var db = new TempDatabase();
        var journal = JournalStand.Create(db.Constructor);
        var shape = JournalStand.Shape(db.Constructor, journal);
        DocRegistrar.Register(db.Constructor, shape,
            new RegistrationInput("Кронштейн", "АБВГ", "301121", "Иванов Иван Иванович", "Машина"),
            Now.LocalDateTime);
        var context = Context(db, journal);

        Assert.StartsWith("<b>Подтвердите удаление документа:</b>\nНомер: <b>АБВГ.301121.001</b>\n",
                          Run(context, "Действие: Удаление\nНомер: АБВГ.301121.001").Reply, StringComparison.Ordinal);
        Assert.Equal("<b>Операция отменена</b>", Run(context, "Нет").Reply);

        Run(context, "Действие: Удаление\nНомер: АБВГ.301121.001");
        Assert.StartsWith("<b>Удалено</b>\nНомер: <b>АБВГ.301121.001</b>\n", Run(context, "Да").Reply,
                          StringComparison.Ordinal);
    }

    // ── Инструкция ────────────────────────────────────────────────────────────

    /// <summary>
    /// Слова инструкции сверяются с читаемым образцом: оформление не меняет содержание.
    /// </summary>
    [Fact]
    public void The_instruction_keeps_its_words()
    {
        Assert.Equal(Words(PlainInstruction), Words(ReplyMarkup.Text(Instruction())));
    }

    /// <summary>
    /// Внутри цитаты и списка нет «\n»: сценарий заменил бы его переносом, и в блоке
    /// появились бы пустые строки.
    /// </summary>
    [Fact]
    public void Blocks_hold_no_line_feeds()
    {
        var instruction = Instruction();
        var blocks = Regex.Matches(instruction, "<(blockquote|ul)>.*?</\\1>", RegexOptions.Singleline);

        Assert.Equal(19, blocks.Count);
        Assert.All(blocks, block => Assert.DoesNotContain('\n', block.Value));
        Assert.DoesNotContain("\n<blockquote>", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("\n<ul>", instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void The_instruction_has_its_heading_sections_and_answers_marked()
    {
        var instruction = Instruction();

        Assert.StartsWith("<b><font size='4'>Инструкция по работе с журналом КД</font></b>\n", instruction,
                          StringComparison.Ordinal);
        foreach (var section in new[] { "1. Регистрация нового документа", "2. Просмотр документа",
                                        "3. Удаление документа", "4. Изменение документа",
                                        "Правила подтверждения", "Если возникла ошибка" })
            Assert.Contains($"<b>{section}</b>", instruction, StringComparison.Ordinal);
        Assert.Contains("<blockquote>Действие: Просмотр<br>Номер: АБВГ.301121.001</blockquote>", instruction,
                        StringComparison.Ordinal);
        Assert.Contains("Напишите отдельным сообщением <b>Да</b> для регистрации или <b>Нет</b> для отмены.",
                        instruction, StringComparison.Ordinal);
        Assert.Contains("например <i>АБВГ.301121.001-01</i>.", instruction, StringComparison.Ordinal);
        Assert.Contains("<blockquote><b>Номер:</b> документ <b>АБВГ.301121.001</b> не найден</blockquote>",
                        instruction, StringComparison.Ordinal);
        Assert.EndsWith("<blockquote>Инструкция</blockquote>", instruction, StringComparison.Ordinal);
    }

    /// <summary>Образцы регистрации из инструкции — цитатой, какой ее разметку пришлет программа.</summary>
    private static IEnumerable<string> RegistrationSamples() =>
        Regex.Matches(Instruction(), "<blockquote>(Действие: Регистрация.*?)</blockquote>")
             .Select(m => m.Value);

    /// <summary>
    /// Человек копирует образец из оформленной инструкции, и сообщение приходит в одном из видов:
    /// разметкой той же цитаты, строками, как их видно на экране, или с метками цитаты и списка
    /// перед строками. Разбор читает все.
    /// </summary>
    [Theory]
    [InlineData("markup")]
    [InlineData("div")]
    [InlineData("lines")]
    [InlineData("quoted")]
    [InlineData("bulleted")]
    public void A_sample_copied_from_the_instruction_is_read(string how)
    {
        var samples = RegistrationSamples().ToList();
        Assert.Equal(2, samples.Count);

        foreach (var sample in samples)
        {
            var lines = ReplyMarkup.Text(sample).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var text = how switch
            {
                "markup"   => sample,
                "div"      => "<div>" + sample.Replace("<br>", "</div><div>") + "</div>",
                "lines"    => string.Join("\n", lines),
                "quoted"   => string.Join("\n", lines.Select(l => "> " + l)),
                _          => string.Join("\n", lines.Select(l => "\u2022\u00a0" + l)),
            };

            var (request, problems) = MessageRegistration.Read(text, ["АБВГ"]);

            Assert.Empty(problems);
            Assert.Equal("Кронштейн", request!.Name);
            Assert.Equal("Машина", request.Project);
            Assert.Equal("Иванов Иван Иванович", request.Fio);
        }
    }

    /// <summary>Скопированный образец изменения тоже читается — по нему строится запрос.</summary>
    [Fact]
    public void A_copied_edit_sample_is_read()
    {
        var sample = Regex.Match(Instruction(), "<blockquote>(Действие: Изменение.*?)</blockquote>").Value;

        Assert.Equal(5, MessageRegistration.KnownLines(sample));
        Assert.Equal(5, MessageRegistration.KnownLines("> " + ReplyMarkup.Text(sample).Trim().Replace("\n", "\n> ")));
    }

    /// <summary>Инструкция до оформления — образец ее слов.</summary>
    private const string PlainInstruction = """
        Инструкция по работе с журналом КД

        Доступны четыре действия: регистрация, просмотр, удаление и изменение.
        Каждое поле пишите с новой строки в формате Поле: значение. Вместо данных из примеров указывайте свои.

        1. Регистрация нового документа

        Действие: Регистрация
        Номер: АБВГ.301121.ХХХ
        Наименование: Кронштейн
        Проект: Машина
        ФИО: Иванов Иван Иванович

        Все пять полей обязательны. ФИО указывайте полностью.
        Вместо ХХХ программа подставит первый свободный номер и пришлет результат регистрации.
        Можно указать и полный свободный номер, например АБВГ.301121.001. Занятый номер повторно не регистрируется.
        Если в журнале еще нет документов с таким кодом организации и классификации, программа покажет расшифровку кода ЕСКД и попросит подтверждение. Так бывает с первым документом серии — по маске, полным номером или исполнением.
        Если в новой серии код не найден в доступном справочнике ЕСКД, программа предупредит: возможно, код относится к закрытому классификатору, и также попросит подтверждение.
        Напишите отдельным сообщением Да для регистрации или Нет для отмены. До ответа Да запись не создается.
        Последующие документы существующей серии регистрируются без этого предупреждения и дополнительного подтверждения.
        Если серия изменилась за время ожидания, регистрация не выполняется: отправьте запрос заново.

        Для нескольких документов перечислите номера через запятую в той же строке:

        Номер: АБВГ.301121.ХХХ, АБВГ.806121.ХХХ, АБВГ.301121.ХХХ

        В одной заявке не больше 100 номеров. Наименование, проект и ФИО общие для всего списка. Повтор маски запрашивает еще один свободный номер.
        Если в одном номере ошибка, не регистрируется весь список. Если требуется подтверждение, один ответ Да или Нет относится ко всему списку.
        При настроенном приеме вложений можно отправить книгу .xlsx или .xlsm по шаблону журнала:
        Первый лист, восемь колонок журнала, до 1000 строк и 10 МБ. Наименование, проект и ФИО указываются в каждой строке.
        Ошибка отклоняет весь файл; ответ Да или Нет подтверждает либо отменяет весь список. В ответ придут номера текстом, исходная книга не изменяется.
        Для регистрации следующего исполнения уже выданного документа:

        Действие: Регистрация
        Номер: АБВГ.301121.001-ХХ
        Наименование: Кронштейн
        Проект: Машина
        ФИО: Иванов Иван Иванович

        Вместо ХХ будет выдано свободное исполнение, например 001-01.
        Повторная отправка заявки на регистрацию создает новую запись. Не отправляйте ее повторно, если уже получили успешный ответ.

        2. Просмотр документа

        Действие: Просмотр
        Номер: АБВГ.301121.001

        В ответ придут все данные записи журнала. Подтверждение не требуется.
        Для просмотра конкретного исполнения указывайте полный номер, например АБВГ.301121.001-01.

        3. Удаление документа

        Действие: Удаление
        Номер: АБВГ.301121.001

        Программа найдет документ, покажет его данные и попросит подтверждение.
        Отправьте отдельным сообщением Да, чтобы удалить запись, или Нет, чтобы отменить операцию.
        До подтверждения запись не удаляется. После удаления освободившийся номер может быть выдан снова.

        4. Изменение документа

        Действие: Изменение
        Номер: АБВГ.301121.001
        Наименование: Опора
        Проект: Станок
        ФИО: Петров Петр Петрович

        Указывайте только те поля, которые хотите заменить. Можно менять наименование, проект и ФИО. Остальные значения сохраняются.
        Например, для изменения только проекта:

        Действие: Изменение
        Номер: АБВГ.301121.001
        Проект: Станок

        Программа покажет текущие данные и предлагаемые изменения. Отправьте Да, чтобы сохранить, или Нет, чтобы отменить.
        Номер и дата регистрации не меняются. Пустые значения не принимаются.

        Правила подтверждения

        Подтверждение действует 10 минут и используется один раз.
        Регистр ответа не важен: Да, да, ДА подтверждают; Нет, нет, НЕТ отменяют.
        Ответить должен тот же отправитель через тот же источник сообщений и сценарий, для той же базы и журнала.
        Пока операция ожидает подтверждения, новую начать нельзя. Сначала ответьте Да или Нет. Просмотр и Инструкция остаются доступны.
        После перезапуска программы запрос на удаление или изменение нужно отправить заново.
        Если документ за время ожидания изменился или был удален, операция не выполнится. Отправьте запрос заново.

        Если возникла ошибка

        Если номер не найден при просмотре, удалении или изменении, ответ будет таким:
        Номер: документ АБВГ.301121.001 не найден
        Подтверждение в этом случае не запрашивается, операция не выполняется.
        Если в заявке есть ошибки, программа перечислит их в ответе. Исправьте поля и отправьте запрос целиком.

        Чтобы снова получить эту инструкцию, отправьте отдельным сообщением:
        Инструкция
        """;
}
