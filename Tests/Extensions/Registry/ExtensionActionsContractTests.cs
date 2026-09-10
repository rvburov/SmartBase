using SmartBase.Extensions;

namespace SmartBase.Tests.Extensions.Registry;

/// <summary>
/// Контракт объявленных действий по всем расширениям (<see cref="IExtension.Actions"/>). Шаг
/// сценария строится целиком из этого объявления, а зовет его оболочка — поэтому кривое
/// объявление ломает не расширение, а сценарий, который однажды ночью просто ничего не сделает.
///
/// Правило файла то же, что у соседей: ни один тест не называет конкретное расширение. Реестр
/// без единого действия дает пустые проходы — это законное состояние.
/// </summary>
public sealed class ExtensionActionsContractTests
{
    private static IEnumerable<(IExtension Extension, IExtensionAction Action)> Actions() =>
        ExtensionRegistry.All.SelectMany(e => e.Actions.Select(a => (Extension: e, Action: a)));

    // Дефолт интерфейса (Actions => []) обязан отдавать пустой список, а не null: сценарии
    // обходят его без проверок.
    [Fact]
    public void Extension_without_actions_declares_an_empty_list()
    {
        foreach (var extension in ExtensionRegistry.All)
            Assert.NotNull(extension.Actions);
    }

    // Ключ действия попадает в шаг сценария и лежит в app.db: два одинаковых ключа означают, что
    // сценарий однажды позовет не то действие.
    [Fact]
    public void Action_keys_are_unique_within_an_extension()
    {
        foreach (var extension in ExtensionRegistry.All)
        {
            var keys = extension.Actions.Select(a => a.Key).ToList();
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void Actions_have_key_name_and_description()
    {
        foreach (var (extension, action) in Actions())
        {
            Assert.False(string.IsNullOrWhiteSpace(action.Key),
                         $"Пустой ключ действия у {extension.Id}");
            Assert.False(string.IsNullOrWhiteSpace(action.Name),
                         $"Безымянное действие у {extension.Id} — в шаге сценария его не выбрать");
            Assert.False(string.IsNullOrWhiteSpace(action.Description),
                         $"Действие «{action.Name}» ({extension.Id}) не говорит, что делает");
        }
    }

    // Поля действия раскладываются на колонки источника: без ключа и подписи раскладка
    // получается безымянной, а два одинаковых ключа сводят две колонки в одно значение.
    [Fact]
    public void Action_fields_have_key_and_label_and_do_not_repeat()
    {
        foreach (var (extension, action) in Actions())
        {
            foreach (var field in action.Fields)
            {
                Assert.False(string.IsNullOrWhiteSpace(field.Key),
                             $"Пустой ключ поля у действия «{action.Name}» ({extension.Id})");
                Assert.False(string.IsNullOrWhiteSpace(field.Label),
                             $"Пустая подпись поля «{field.Key}» у «{action.Name}» ({extension.Id})");
            }

            var keys = action.Fields.Select(f => f.Key).ToList();
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        }
    }

    /// <summary>
    /// Операция получает ЗАПИСЬ и обязана сказать, из чего та состоит: без объявленных полей
    /// оболочке нечего раскладывать, и шаг молча получал бы пустой вход.
    /// </summary>
    [Fact]
    public void An_operation_declares_what_it_needs()
    {
        foreach (var (extension, action) in Actions())
            if (action.Kind == ExtensionActionKind.Operation)
                Assert.True(action.Fields.Count > 0,
                            $"Операция «{action.Name}» ({extension.Id}) не объявила ни одного поля");
    }

    /// <summary>
    /// Преобразователь получает текст или файл ЦЕЛИКОМ, и полей у него нет: настройка разбора —
    /// это <see cref="IExtension.Settings"/>, а не вход, который оболочка раскладывает по
    /// колонкам источника.
    /// </summary>
    [Fact]
    public void A_transformer_takes_the_input_whole()
    {
        foreach (var (extension, action) in Actions())
            if (action.Kind == ExtensionActionKind.Transformer)
                Assert.True(action.Fields.Count == 0,
                            $"Преобразователь «{action.Name}» ({extension.Id}) объявил поля — "
                            + "разбор настраивается настройками расширения");
    }
}
