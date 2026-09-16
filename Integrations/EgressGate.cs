using System.Net;
using SmartBase.Core.Models;

namespace SmartBase.Integrations;

/// <summary>Почему наружу нельзя. Код нужен журналу и экрану: текст меняется, код — нет.</summary>
public enum EgressDenyReason
{
    /// <summary>Запрета нет — обращение разрешено.</summary>
    None = 0,

    /// <summary>Сборка для закрытого контура: исходящих в ней нет по построению.</summary>
    ClosedLoopBuild,

    /// <summary>Политика организации не найдена или не прочиталась.</summary>
    NoPolicy,

    /// <summary>Срок действия политики истек.</summary>
    PolicyExpired,

    /// <summary>Изолированный режим: исходящих нет вовсе.</summary>
    IsolatedMode,

    /// <summary>Схема адреса не поддерживается (разрешены только http и https).</summary>
    SchemeNotAllowed,

    /// <summary>Адрес не разобрался.</summary>
    BadAddress,

    /// <summary>Имя узла не входит в белый список.</summary>
    HostNotAllowed,

    /// <summary>Фактический адрес соединения не входит в разрешенные сети.</summary>
    AddressNotAllowed,

    /// <summary>Внутренний режим, а имя разрешилось в публичный адрес.</summary>
    PublicAddressInInternalMode,

    /// <summary>Путь лежит вне разрешенных политикой корней.</summary>
    FolderNotAllowed,

    /// <summary>База помечена закрытым контуром: исходящих у нее нет.</summary>
    DatabaseIsClosed,

    /// <summary>У свойства стоит метка конфиденциальности: наружу оно не уходит.</summary>
    FieldIsLabeled,

    /// <summary>Выбран потолок запусков в час.</summary>
    TooManyRuns,

    /// <summary>Выбран потолок объема за сутки.</summary>
    TooManyBytes,

    /// <summary>Прокси организации записан в политике так, что им нельзя воспользоваться.</summary>
    BadProxy,

    /// <summary>Два источника политики назвали РАЗНЫЕ прокси — ходить не через что.</summary>
    ProxyConflict,
}

/// <summary>
/// Сколько уже израсходовано — накопительные счетчики для квот политики. Считает их журнал
/// запусков, а решает <see cref="EgressGate"/>: в шлюзе нет ввода-вывода, и его решения обязаны
/// проверяться без единого файла.
///
/// Не подключен ни один счетчик — расход считается нулевым. Это не дыра: без журнала нет и
/// обмена, который в него пишет, а запирать шлюз из-за отсутствия счетчика значило бы ломать
/// тесты и первые запуски на пустой машине.
/// </summary>
public interface IEgressUsage
{
    /// <summary>
    /// Сколько засчитанных запусков было с указанного момента — по всему обмену сразу. Потолок
    /// запусков в час общий намеренно: он про то, как часто программа вообще стучится наружу, а
    /// не про то, куда именно.
    /// </summary>
    int RunsSince(DateTimeOffset moment);

    /// <summary>
    /// Сколько байт прошло с указанного момента ПО ЭТОМУ направлению.
    ///
    /// Направление — карточка запроса (ее ключ), потому что подтверждают и отзывают именно ее
    /// (раздел 10.8 дорожной карты). Общая сумма по всему обмену тут не годится дважды: читающий
    /// опрос каждые пять минут съедал бы потолок, заведенный против выноса данных, а один шумный
    /// обмен останавливал бы все остальные, настроенные правильно.
    /// </summary>
    /// <param name="moment">С какого момента считать.</param>
    /// <param name="direction">Ключ карточки запроса; 0 — обмен без карточки.</param>
    long BytesSince(DateTimeOffset moment, int direction);

    /// <summary>
    /// Когда из скользящего окна выпадет САМЫЙ СТАРЫЙ засчитанный расход — то есть когда потолок
    /// начнет отпускать. <c>null</c> — расхода в окне нет, ждать нечего.
    ///
    /// Нужно ровно для одного: сказать человеку, КОГДА обмен пойдет снова. Отказ «выбран
    /// суточный потолок» без этого ответа выглядит как поломка — обмен молча перестал работать,
    /// и непонятно, чинить его или ждать.
    /// </summary>
    /// <param name="now">Текущий момент.</param>
    /// <param name="direction">Ключ карточки запроса; 0 — обмен без карточки.</param>
    /// <param name="window">Ширина окна: сутки для объема, час для числа запусков.</param>
    DateTimeOffset? FreesAt(DateTimeOffset now, int direction, TimeSpan window);
}

/// <summary>Решение шлюза: пускать или нет, почему и что показать человеку.</summary>
/// <param name="Allowed">Разрешено ли обращение.</param>
/// <param name="Reason">Код запрета; у разрешения — <see cref="EgressDenyReason.None"/>.</param>
/// <param name="Explanation">Текст для человека и для журнала — без секретов.</param>
public sealed record EgressDecision(bool Allowed, EgressDenyReason Reason, string Explanation)
{
    private static readonly EgressDecision Ok = new(true, EgressDenyReason.None, "разрешено политикой");

    public static EgressDecision Allow() => Ok;

    /// <summary>
    /// Разрешено, но НЕ белым списком. Текст уезжает в журнал безопасности, и там разница
    /// принципиальна: «разрешено политикой» и «политика позволила пользователю дописать адрес
    /// самому» — два разных ответа проверяющему на вопрос, кто решил пустить этот запрос.
    /// </summary>
    public static EgressDecision AllowBecause(string explanation) =>
        new(true, EgressDenyReason.None, explanation);

    public static EgressDecision Deny(EgressDenyReason reason, string explanation) =>
        new(false, reason, explanation);
}

/// <summary>
/// ЕДИНСТВЕННАЯ дверь наружу. Через нее проходит любое обращение к сторонней программе: и сетевое,
/// и запись в чужую папку. Смысл не в удобстве, а в проверяемости — на вопрос «где программа
/// выходит наружу» ответ должен быть «один файл», а не «надо посмотреть весь проект»
/// (docs/ROADMAP-API.md, раздел 10.2).
///
/// Держится это тремя вещами, и одной мало:
/// <list type="number">
///   <item>сам шлюз — все решения принимаются здесь;</item>
///   <item>сторож <c>Tests/Unit/OneDoorOutwardTests</c> — сетевые типы не используются больше
///         нигде в решении, включая <c>Extensions/</c>;</item>
///   <item>сборка для закрытого контура (<c>-p:ClosedLoop=true</c>) — в ней исходящих нет по
///         построению, и это видно в манифесте сборки.</item>
/// </list>
///
/// Состояние по умолчанию — <see cref="EgressPolicySet.Closed"/>: пока загрузчик не принес
/// политику, наружу нельзя ничего. Порядок именно такой, а не «сначала работает, потом
/// ограничим»: иначе окно между стартом и загрузкой политики становится дырой.
/// </summary>
public static class EgressGate
{
    /// <summary>Разрешенные схемы. Все остальное (file, ftp, ws) — запрет без разговоров.</summary>
    private static readonly string[] AllowedSchemes = ["http", "https"];

    private static EgressPolicySet _policy = EgressPolicySet.Closed;

    /// <summary>Действующая политика. До вызова <see cref="AttachPolicy"/> — самая строгая.</summary>
    public static EgressPolicySet Policy => _policy;

    /// <summary>
    /// Подключить прочитанную политику. Зовет загрузчик на старте и при перечитывании; тесты
    /// подставляют свою. Прием тот же, что у <c>AppSettings.AttachStore</c>: контейнера
    /// зависимостей в приложении нет, а связывать шлюз с чтением файлов незачем.
    /// </summary>
    public static void AttachPolicy(EgressPolicySet policy) => _policy = policy;

    private static IEgressUsage? _usage;

    /// <summary>
    /// Подключить счетчик расхода — журнал запусков. Тем же приемом, что и политика: шлюз
    /// решает, журнал считает, и связывать одно с другим напрямую незачем.
    /// </summary>
    public static void AttachUsage(IEgressUsage? usage) => _usage = usage;

    /// <summary>Вернуть шлюз в состояние по умолчанию — наружу нельзя ничего.</summary>
    public static void Close()
    {
        _policy = EgressPolicySet.Closed;
        _usage  = null;
    }

    /// <summary>
    /// Собрана ли программа для закрытого контура. Показывается в «О программе» и в паспорте
    /// интеграций: человеку и проверяющему нужен ответ на месте, а не сверка манифеста.
    /// </summary>
    public static bool ClosedLoopBuild =>
#if CLOSED_LOOP
        true;
#else
        false;
#endif

    /// <summary>Проверка адреса по имени — до запроса. Сегодняшний день берется системный.</summary>
    public static EgressDecision Check(Uri? address) =>
        Check(address, DateOnly.FromDateTime(DateTime.Today));

    /// <summary>
    /// Проверка адреса по имени — до запроса. День передается явно: срок действия политики
    /// обязан проверяться и в тестах, а тест, зависящий от системных часов, доверия не стоит.
    /// </summary>
    public static EgressDecision Check(Uri? address, DateOnly today)
    {
#if CLOSED_LOOP
        _ = address; _ = today;
        return EgressDecision.Deny(EgressDenyReason.ClosedLoopBuild,
            "сборка для закрытого контура: исходящих обращений в ней нет");
#else
        if (address is null || !address.IsAbsoluteUri)
            return EgressDecision.Deny(EgressDenyReason.BadAddress, "адрес не разобрался");

        var gate = CheckPolicyState(today);
        if (!gate.Allowed) return gate;

        if (!AllowedSchemes.Contains(address.Scheme, StringComparer.OrdinalIgnoreCase))
            return EgressDecision.Deny(EgressDenyReason.SchemeNotAllowed,
                $"схема «{address.Scheme}» не поддерживается: разрешены только http и https");

        if (_policy.AllowsHost(address.Host)) return EgressDecision.Allow();

        // Флаг «пользователю разрешено добавлять адреса» (10.3, по умолчанию выключен). Организация
        // им говорит: белый список на этой машине — подсказка, а не рамка. Пока флаг ничего не
        // делал, включить его было нельзя — он просто врал строкой на экране.
        //
        // Разрешение это НАЗЫВАЕТСЯ в журнале отдельно от обычного: проверяющему важно отличить
        // «адрес был в списке организации» от «списка не хватило, но пускать разрешено».
        if (_policy.UserMayAddHosts)
            return EgressDecision.AllowBecause(
                $"адреса «{address.Host}» нет в списке организации, но политика разрешает "
              + "дописывать адреса на этой машине");

        return EgressDecision.Deny(EgressDenyReason.HostNotAllowed,
            $"адрес «{address.Host}» не входит в белый список организации");
#endif
    }

    /// <summary>
    /// Проверка ФАКТИЧЕСКОГО адреса соединения — после того, как имя разрешилось. Вторая
    /// проверка обязательна: между сверкой имени и соединением запись DNS может смениться, и
    /// белый список по именам сам по себе не значит ничего (docs/ROADMAP-API.md, 10.5).
    /// Соединяться нужно ровно с тем адресом, который сюда передан.
    /// </summary>
    public static EgressDecision CheckResolved(IPAddress? address, DateOnly today)
    {
#if CLOSED_LOOP
        _ = address; _ = today;
        return EgressDecision.Deny(EgressDenyReason.ClosedLoopBuild,
            "сборка для закрытого контура: исходящих обращений в ней нет");
#else
        if (address is null)
            return EgressDecision.Deny(EgressDenyReason.BadAddress, "имя не разрешилось в адрес");

        var gate = CheckPolicyState(today);
        if (!gate.Allowed) return gate;

        var listed = _policy.AllowsAddress(address);

        if (!listed && !_policy.UserMayAddHosts)
            return EgressDecision.Deny(EgressDenyReason.AddressNotAllowed,
                $"адрес {address} не входит в разрешенные сети организации");

        // Внутренний режим держится на приватности адреса, а не на списке имен: имя из списка,
        // разрешившееся в публичный адрес, — ровно тот случай, ради которого проверка вторая.
        //
        // Режим НЕ уступает флагу «пользователю разрешено добавлять адреса», и это не
        // непоследовательность: список — рамка организации вокруг адресов, а режим — рамка вокруг
        // самой машины. Разреши флаг обходить и режим, и «внутренний контур» на машине с этим
        // флагом перестал бы быть внутренним вовсе.
        if (_policy.Mode == EgressMode.Internal && !NetworkAddress.IsPrivate(address))
            return EgressDecision.Deny(EgressDenyReason.PublicAddressInInternalMode,
                $"внутренний контур: адрес {address} не из сетей организации");

        return listed
            ? EgressDecision.Allow()
            : EgressDecision.AllowBecause(
                $"адреса {address} нет в сетях организации, но политика разрешает дописывать "
              + "адреса на этой машине");
#endif
    }

    /// <inheritdoc cref="CheckResolved(IPAddress?, DateOnly)"/>
    public static EgressDecision CheckResolved(IPAddress? address) =>
        CheckResolved(address, DateOnly.FromDateTime(DateTime.Today));

    /// <summary>
    /// Через какой прокси ходить наружу (docs/ROADMAP-API.md, 10.5). <c>null</c> при разрешенном
    /// решении — прямое соединение; это состояние по умолчанию.
    ///
    /// <b>Прокси называет только политика.</b> Своего пользователь указать не может, и это не
    /// придирка: через прокси проходит все содержимое запроса целиком, поэтому выбор прокси
    /// равносилен выбору того, кто это содержимое увидит. Такие решения принимает организация.
    ///
    /// <b>Два источника, назвавшие разные прокси, — отказ, а не выбор одного из них.</b> Разные
    /// прокси означают, что две инстанции не договорились, и пойти через любой значило бы решить
    /// за них, кому показать данные. Правило то же, что у мастер-пароля: несогласие источников
    /// запирает дверь, а не открывает ее наполовину.
    ///
    /// Адрес прокси проверяется здесь на разбираемость и схему; его ФАКТИЧЕСКИЙ адрес
    /// проверяется отдельно, там же, где и всякий другой, — <see cref="CheckResolved"/>. Иначе
    /// подмена DNS на имя прокси уводила бы весь исходящий поток куда угодно, и белый список
    /// адресов не значил бы ничего.
    /// </summary>
    /// <param name="proxy">Адрес прокси; <c>null</c> — прямое соединение.</param>
    public static EgressDecision CheckProxy(out Uri? proxy)
    {
        proxy = null;

        var named = _policy.Proxies;

        if (named.Count == 0) return EgressDecision.Allow();

        if (named.Count > 1)
            return EgressDecision.Deny(EgressDenyReason.ProxyConflict,
                "источники политики назвали разные прокси организации ("
              + string.Join(", ", named) + "): исходящие запрещены, пока они не сойдутся");

        if (!Uri.TryCreate(named[0], UriKind.Absolute, out var parsed))
            return EgressDecision.Deny(EgressDenyReason.BadProxy,
                $"адрес прокси «{named[0]}» в политике не разобрался: "
              + "ожидался вид http://proxy.example:3128");

        if (!AllowedSchemes.Contains(parsed.Scheme, StringComparer.OrdinalIgnoreCase))
            return EgressDecision.Deny(EgressDenyReason.BadProxy,
                $"схема прокси «{parsed.Scheme}» не поддерживается: разрешены только http и https");

        proxy = parsed;
        return EgressDecision.Allow();
    }

    /// <summary>
    /// Проверка пути, куда сценарию разрешено писать и откуда читать. Выгрузка файлом — такой
    /// же выход наружу, как запрос: съемный диск и личная папка уносят данные не хуже адреса.
    /// Ручной выгрузки человеком это не касается — она остается и лишь пишется в журнал.
    ///
    /// Режим контура сюда НЕ применяется (см. <see cref="CheckPolicyState"/>): изолированный
    /// режим запрещает сетевые обращения, а папка обмена в нем работает — так написано в 10.4 и
    /// в этом весь смысл режима для машины, у которой сети нет. Рамку папкам ставит список
    /// <c>folders</c> политики, и он же остается единственной их рамкой.
    /// </summary>
    public static EgressDecision CheckFolder(string? path, DateOnly today)
    {
        var gate = CheckPolicyState(today, network: false);
        if (!gate.Allowed) return gate;

        if (!_policy.AllowsFolder(path))
            return EgressDecision.Deny(EgressDenyReason.FolderNotAllowed,
                "папка вне корней, разрешенных политикой организации");

        return EgressDecision.Allow();
    }

    /// <inheritdoc cref="CheckFolder(string?, DateOnly)"/>
    public static EgressDecision CheckFolder(string? path) =>
        CheckFolder(path, DateOnly.FromDateTime(DateTime.Today));

    /// <summary>
    /// Не выбраны ли накопительные потолки политики: запусков в час и байт в сутки. Спрашивается
    /// ПЕРЕД обращением, а не после, — потолок, о котором узнают задним числом, ничего не
    /// ограничивает.
    ///
    /// Окна скользящие, а не календарные: «час» — это последние шестьдесят минут, а не «с начала
    /// текущего часа». Календарное окно обнуляется в ноль минут, и тысяча запросов, поставленная
    /// на 59-ю и 61-ю минуту, прошла бы дважды за две минуты.
    ///
    /// Расход считает журнал; счетчик не подключен — расход нулевой (см. <see cref="IEgressUsage"/>).
    ///
    /// <b>Потолок запусков — общий, потолок объема — на направление</b>, ровно как написано в
    /// 10.6. Направление подтверждают и отзывают по одному, мастер-паролем и на срок, поэтому и
    /// счет объема ведется по нему: общая сумма означала бы, что читающий опрос каждые пять
    /// минут выбирает потолок, заведенный против выноса данных, а один шумный обмен останавливает
    /// все прочие, настроенные правильно. Обратная сторона названа честно: суммарный выход машины
    /// равен потолку, умноженному на число направлений, и ограничивает его не это число, а то,
    /// что каждое исходящее направление кто-то подтвердил отдельно.
    /// </summary>
    /// <param name="now">Момент обращения; передается явно — тест на часах не строится.</param>
    /// <param name="direction">Направление — ключ карточки запроса. Обязателен: значение по
    /// умолчанию свалило бы весь новый обмен в общий счет нулевого направления, и потолок
    /// перестал бы значить то, что значит.</param>
    /// <param name="bytes">Сколько байт собирается пройти; 0 — объем заранее неизвестен, и
    /// проверяется только уже израсходованное.</param>
    public static EgressDecision CheckQuota(DateTimeOffset now, int direction, long bytes = 0)
    {
        var gate = CheckPolicyState(DateOnly.FromDateTime(now.LocalDateTime.Date));
        if (!gate.Allowed) return gate;

        if (_usage is not { } usage) return EgressDecision.Allow();

        var hour = TimeSpan.FromHours(1);
        var day  = TimeSpan.FromDays(1);

        var runs = usage.RunsSince(now - hour);
        if (runs >= _policy.MaxRunsPerHour)
            return EgressDecision.Deny(EgressDenyReason.TooManyRuns,
                $"выбран потолок запусков в час: {runs} из {_policy.MaxRunsPerHour}"
              + Resumes(usage, now, direction, hour));

        // Любой обмен стоит хотя бы что-то, поэтому выбранный до нуля остаток — уже отказ, даже
        // когда объем заранее неизвестен. Пропустить запрос ради того, чтобы отклонить его на
        // первой же порции, значит зря потревожить стороннюю программу.
        var spent = usage.BytesSince(now - day, direction);
        if (spent >= _policy.MaxBytesPerDay || spent + Math.Max(0, bytes) > _policy.MaxBytesPerDay)
            return EgressDecision.Deny(EgressDenyReason.TooManyBytes,
                $"выбран суточный потолок объема этого направления: "
              + $"{spent:N0} из {_policy.MaxBytesPerDay:N0} байт"
              + Resumes(usage, now, direction, day));

        return EgressDecision.Allow();
    }

    /// <summary>
    /// Хвост отказа по потолку: когда обмен пойдет снова. Ради него и заведен
    /// <see cref="IEgressUsage.FreesAt"/>.
    ///
    /// Без этой добавки отказ по квоте — худший из возможных: обмен, настроенный и работавший,
    /// молча перестает ходить, и человек не знает, сломалось это или ждет. Строка уезжает и в
    /// журнал запусков, и в журнал безопасности, и на карточку — всюду, где отказ читают.
    ///
    /// Счетчик не ответил — добавки нет. Придумывать время, которого не знаем, хуже, чем
    /// промолчать: обещанное и не сбывшееся «после 14:20» дороже отсутствия обещания.
    /// </summary>
    private static string Resumes(IEgressUsage usage, DateTimeOffset now, int direction,
                                  TimeSpan window)
    {
        if (usage.FreesAt(now, direction, window) is not { } at || at <= now) return "";

        var local = at.ToLocalTime();

        return local.Date == now.ToLocalTime().Date
            ? $"; отпустит после {local:HH:mm}"
            : $"; отпустит после {local:dd.MM HH:mm}";
    }

    /// <summary>
    /// Сколько байт еще можно получить за сутки. Нужно тем, кто качает порциями: спросить
    /// квоту один раз перед запуском мало — выгрузка на двести порций по тридцать мегабайт
    /// перешагнет любой суточный потолок, ни разу его не спросив.
    ///
    /// Счетчик не подключен либо политика запирает контур — <c>0</c> при запрете и
    /// <see cref="long.MaxValue"/> при отсутствии счетчика: считать нечем, а придумывать
    /// ограничение шлюз не должен.
    /// </summary>
    /// <param name="now">Момент обращения.</param>
    /// <param name="direction">Направление — ключ карточки запроса; остаток считается по нему.</param>
    public static long BytesLeft(DateTimeOffset now, int direction)
    {
        if (!CheckPolicyState(DateOnly.FromDateTime(now.LocalDateTime.Date)).Allowed) return 0;

        if (_usage is not { } usage) return long.MaxValue;

        return Math.Max(0,
            _policy.MaxBytesPerDay - usage.BytesSince(now - TimeSpan.FromDays(1), direction));
    }

    /// <summary>
    /// Можно ли вообще что-то отправлять из этой базы. Метка «закрытый контур» запрещает
    /// исходящие независимо от политики, режима и прав: это самый грубый и самый понятный
    /// проверяющему рубеж — секретная база физически не имеет исходящих путей, а обычные базы
    /// продолжают работать со всеми интеграциями.
    ///
    /// Метка «для служебного пользования» обмен НЕ запрещает: иначе ее не поставят ни на одну
    /// рабочую базу и механизм окажется бумажным. Точный запрет — метка на свойстве
    /// (<see cref="CheckField"/>).
    /// </summary>
    public static EgressDecision CheckDatabase(ConfidentialityLabel label, DateOnly today)
    {
        if (label == ConfidentialityLabel.Closed)
            return EgressDecision.Deny(EgressDenyReason.DatabaseIsClosed,
                "база помечена закрытым контуром: исходящие обращения из нее запрещены");

        return CheckPolicyState(today);
    }

    /// <inheritdoc cref="CheckDatabase(ConfidentialityLabel, DateOnly)"/>
    public static EgressDecision CheckDatabase(ConfidentialityLabel label) =>
        CheckDatabase(label, DateOnly.FromDateTime(DateTime.Today));

    /// <summary>
    /// Можно ли подставить это свойство в исходящий шаг. Любая метка строже «Открыто» —
    /// запрет.
    ///
    /// Проверка нужна при НАСТРОЙКЕ, а не при отправке: помеченное поле не должно появляться
    /// в списке привязки вовсе. Узнавать об ограничении из ошибки в журнале — худший из
    /// возможных способов: сценарий уже настроен, человек уверен, что данные уходят, и
    /// молчание выглядит поломкой.
    /// </summary>
    public static EgressDecision CheckField(ConfidentialityLabel label)
    {
        if (label == ConfidentialityLabel.Open) return EgressDecision.Allow();

        return EgressDecision.Deny(EgressDenyReason.FieldIsLabeled,
            $"у свойства метка «{Confidentiality.Title(label)}»: наружу оно не уходит");
    }

    /// <summary>
    /// Общее для всех проверок состояние политики: есть ли она, не истекла ли, не заперт ли
    /// контур. Вынесено отдельно, чтобы ни одна дверь не могла пройти мимо этих вопросов.
    ///
    /// <paramref name="network"/> различает две двери, а не два уровня строгости. Режим контура
    /// (10.4) ограничивает выход В СЕТЬ: «изолированный» означает «ни одного сетевого
    /// обращения», и там же сказано прямо, что в нем «работают локальные шаги: папка-приемник,
    /// запись в базу, уведомления, расширения, экспорт в разрешенные папки». Папки в этом режиме
    /// ограничивает не он, а список <c>folders</c> — его пишет организация, и пустой список
    /// по-прежнему не разрешает ни одной папки.
    ///
    /// Пока режим запирал и папки, изолированное рабочее место — то самое, под которое режим и
    /// назначен, — оставалось без ЕДИНСТВЕННОГО доступного ему транспорта: файл в папку обмена
    /// не клался и не читался, хотя папка была перечислена в политике.
    ///
    /// Первые два вопроса общие для обеих дверей: политики нет — корней папок назвать некому,
    /// политика истекла — верить ее спискам больше нельзя.
    /// </summary>
    /// <param name="today">Сегодняшняя дата: срок действия политики.</param>
    /// <param name="network">Проверяется выход в сеть (по умолчанию) либо файловый путь.</param>
    private static EgressDecision CheckPolicyState(DateOnly today, bool network = true)
    {
        if (!_policy.Any)
            return EgressDecision.Deny(EgressDenyReason.NoPolicy,
                network
                    ? "политика организации не найдена: исходящие обращения запрещены"
                    : "политика организации не найдена: папки для обмена файлами не заданы");

        if (_policy.Expired(today))
            return EgressDecision.Deny(EgressDenyReason.PolicyExpired,
                $"срок действия политики истек {_policy.ValidUntil:dd.MM.yyyy}");

        if (network && _policy.Mode == EgressMode.Isolated)
            return EgressDecision.Deny(EgressDenyReason.IsolatedMode,
                "изолированный режим: исходящие обращения запрещены");

        return EgressDecision.Allow();
    }
}
