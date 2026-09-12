using System.Globalization;

namespace SmartBase.Integrations;

/// <summary>
/// Выдача чтения — «роботу X можно читать коллекцию Y, поля такие-то, до такого-то числа»
/// (docs/ROADMAP-API.md, шаг 10).
///
/// <b>Почему чтение требует выдачи, а запись — только маски.</b> Ответ на <c>GET</c> — это
/// данные, ПОКИДАЮЩИЕ программу, и без своего замка он стал бы второй дверью наружу мимо
/// направлений, меток и журнала. Поэтому чтение заперто тем же замком, что отправка: выдачу
/// подтверждает мастер-пароль организации, у нее есть срок и отсчет простоя, а каждый ответ
/// ложится в журнал безопасности и занимает суточный потолок.
///
/// Зеркало <see cref="OutgoingDirection"/> — сроки и правила простоя те же и намеренно: два
/// разных распорядка на один вопрос «кому можно выносить данные» разошлись бы в сторону
/// разрешения.
///
/// <b>Отпечатка здесь нет</b> — в отличие от направления. Направление подтверждает ЧУЖУЮ
/// карточку запроса, которую потом правят, и правка обязана ронять разрешение. Выдача же сама
/// и есть источник истины: коллекция и поля названы в ней, править между подтверждением и
/// проверкой нечего. Единственное, что меняется за ее спиной, — метки полей, и их проверяет
/// само чтение при каждом ответе.
/// </summary>
/// <param name="Id">Ключ строки в app.db.</param>
/// <param name="TokenId">Токен, которому разрешено читать.</param>
/// <param name="CollectionId">Коллекция — ключом: имя может смениться, выдача остается.</param>
/// <param name="Collection">Имя коллекции на момент выдачи — для экрана и паспорта.</param>
/// <param name="FieldKeys">Что отдавать: <c>t</c> — название записи, число — ключ свойства.
/// Источник истины; имена полей берутся живыми при каждом чтении.</param>
/// <param name="Fields">Имена полей на момент выдачи — для экрана и паспорта.</param>
/// <param name="Confirmed">Кто подтвердил.</param>
/// <param name="At">Когда подтверждена.</param>
/// <param name="Until">До какого момента действует.</param>
/// <param name="UsedAt">Когда читали в последний раз; <c>null</c> — ни разу.</param>
public sealed record ReadGrant(
    int             Id,
    int             TokenId,
    int             CollectionId,
    string          Collection,
    IReadOnlyList<string> FieldKeys,
    string          Fields,
    string          Confirmed,
    DateTimeOffset  At,
    DateTimeOffset  Until,
    DateTimeOffset? UsedAt = null)
{
    /// <summary>Ключ «названия записи» в списке полей выдачи.</summary>
    public const string TitleKey = "t";

    /// <summary>Через сколько дней простоя выдача отключается сама — как у направления.</summary>
    public const int IdleDays = OutgoingDirection.IdleDays;

    /// <summary>Насколько вперед можно подтвердить — как у направления.</summary>
    public const int MaxDays = OutgoingDirection.MaxDays;

    /// <summary>Момент, от которого считается простой: последнее чтение либо подтверждение.</summary>
    public DateTimeOffset Since => UsedAt ?? At;

    /// <summary>
    /// Почему выдача НЕ действует прямо сейчас; <c>null</c> — действует. Причина словами и
    /// одна: отправитель, у которого чтение встало, должен увидеть «вышел срок», а не общее
    /// «не разрешено».
    /// </summary>
    public string? Trouble(DateTimeOffset now)
    {
        if (FieldKeys.Count == 0)
            return "в выдаче не назван ни один столбец";

        if (now > Until)
            return $"срок выдачи вышел {Until.ToLocalTime():dd.MM.yyyy}";

        if (now - Since > TimeSpan.FromDays(IdleDays))
            return $"выдачей не пользовались больше {IdleDays} дней, и она отключилась";

        return null;
    }

    /// <summary>Одной строкой для карточки: до какого числа и кем подтверждена.</summary>
    public string Describe() =>
        $"подтвердил {Confirmed}, до {Until.ToLocalTime():dd.MM.yyyy}";

    // ── Ключи полей ───────────────────────────────────────────────────────────

    /// <summary>Строка хранения ключей: <c>t,12,15</c>.</summary>
    public static string PackKeys(IEnumerable<string> keys) => string.Join(",", keys);

    /// <summary>Ключи из строки хранения. Мусор отбрасывается: испорченная правкой app.db
    /// строка не должна расширять выдачу.</summary>
    public static IReadOnlyList<string> UnpackKeys(string? text) =>
        [.. (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(k => k == TitleKey
                     || int.TryParse(k, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                        && id > 0)];
}
