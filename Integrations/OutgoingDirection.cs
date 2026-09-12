namespace SmartBase.Integrations;

/// <summary>
/// Исходящее направление — подтвержденное разрешение конкретному запросу ОТПРАВЛЯТЬ данные
/// наружу (docs/ROADMAP-API.md, 10.8).
///
/// Белый список отвечает на вопрос «куда можно ходить», а направление — на другой: «этому
/// обмену разрешено уносить данные». Первое задает организация одинаково для всех, второе
/// подтверждается по одному, мастер-паролем и на срок.
///
/// Три правила, и каждое закрывает свой способ обойти согласование.
///
/// <list type="number">
///   <item><b>Правка сбрасывает подтверждение.</b> Направление помнит ОТПЕЧАТОК того, что
///   подтверждали, — метод, путь, заголовки и тело. Поменяли хоть знак — отпечаток разошелся, и
///   направление молчит до нового подтверждения. Иначе согласовывают одно, а работает другое:
///   подтвердить безобидный запрос и дописать в него выгрузку — самый дешевый обход.</item>
///   <item><b>Срок.</b> Разрешение, выданное один раз навсегда, через год не помнит никто, а
///   работает оно по-прежнему.</item>
///   <item><b>Автоотключение по простою.</b> Направление, которым не пользовались
///   <see cref="IdleDays"/> дней, выключается само. Настроенный и забытый обмен — открытая дверь,
///   о которой никто не думает; вернуть его стоит одного подтверждения, а не заметить его нельзя
///   никак.</item>
/// </list>
///
/// Модель без ввода-вывода: хранение — <c>IntegrationsRegistry</c>, проверка пароля — политика,
/// запись в журнал безопасности — слой выше. Здесь только «действует ли и почему нет».
/// </summary>
/// <param name="RequestId">Запрос, которому разрешено отправлять.</param>
/// <param name="Fingerprint">Отпечаток запроса на момент подтверждения.</param>
/// <param name="Confirmed">Кто подтвердил — как его знает программа.</param>
/// <param name="At">Когда подтвердили.</param>
/// <param name="Until">До какого момента действует включительно.</param>
/// <param name="UsedAt">Когда направлением пользовались в последний раз; <c>null</c> — ни разу.</param>
public sealed record OutgoingDirection(
    int             RequestId,
    string          Fingerprint,
    string          Confirmed,
    DateTimeOffset  At,
    DateTimeOffset  Until,
    DateTimeOffset? UsedAt = null)
{
    /// <summary>
    /// Через сколько дней простоя направление отключается само. Месяц — не круглое число ради
    /// красоты: обмен, который не понадобился ни разу за месяц, либо настраивали впрок, либо о
    /// нем забыли, и оба случая лечатся одинаково.
    /// </summary>
    public const int IdleDays = 30;

    /// <summary>
    /// Насколько вперед можно подтвердить. Год — предел, за которым «подтверждено» перестает
    /// означать «кто-то посмотрел и согласился».
    /// </summary>
    public const int MaxDays = 365;

    /// <summary>Подтвердить запрос на указанный срок.</summary>
    /// <param name="outgoing">Отпечаток состава исходящей привязки; пусто — запрос ничего из
    /// коллекции не уносит. Подтверждают запрос ВМЕСТЕ с составом полей.</param>
    public static OutgoingDirection Confirm(IntegrationRequest request, string who,
                                            DateTimeOffset now, int days,
                                            string outgoing = "") =>
        new(request.Id, request.Fingerprint(outgoing), who, now,
            now.AddDays(Math.Clamp(days, 1, MaxDays)));

    /// <summary>Момент, от которого считается простой: последнее использование либо подтверждение.</summary>
    public DateTimeOffset Since => UsedAt ?? At;

    /// <summary>
    /// Почему направление НЕ действует прямо сейчас; <c>null</c> — действует.
    ///
    /// Причина возвращается словами и одна: человек, у которого обмен встал, должен увидеть
    /// «вышел срок» или «запрос правили», а не общее «не разрешено».
    /// </summary>
    public string? Trouble(IntegrationRequest request, DateTimeOffset now, string outgoing = "")
    {
        if (request.Id != RequestId) return "подтверждение выдано другому запросу";

        if (Fingerprint != request.Fingerprint(outgoing))
            return "запрос правили после подтверждения — нужно подтвердить заново";

        if (now > Until)
            return $"срок подтверждения вышел {Until.ToLocalTime():dd.MM.yyyy}";

        if (now - Since > TimeSpan.FromDays(IdleDays))
            return $"направлением не пользовались больше {IdleDays} дней, и оно отключилось";

        return null;
    }

    /// <summary>Действует ли направление для этого запроса.</summary>
    public bool Works(IntegrationRequest request, DateTimeOffset now, string outgoing = "") =>
        Trouble(request, now, outgoing) is null;

    /// <summary>Одной строкой для карточки: до какого числа и кем подтверждено.</summary>
    public string Describe() =>
        $"подтвердил {Confirmed}, до {Until.ToLocalTime():dd.MM.yyyy}";
}
