namespace SmartBase.Integrations;

/// <summary>
/// Как программа слушает входящие обращения (docs/ROADMAP-API.md, раздел 6).
///
/// Порядок значений значим, как и у режимов контура: чем меньше число, тем меньше открыто, и
/// на этом держится правило «состояние по умолчанию — самое закрытое».
/// </summary>
public enum IngressMode
{
    /// <summary>Никого не слушаем. Состояние по умолчанию: порт открывают осознанно.</summary>
    Off = 0,

    /// <summary>Только программы этой же машины: <c>127.0.0.1</c>. С соседнего ПК не достучаться
    /// физически, даже зная токен, — прав администратора при этом не требуется.</summary>
    Loopback = 1,

    /// <summary>Адрес машины в локальной сети. Требует разрешения политики организации и прав
    /// администратора один раз (<c>urlacl</c> плюс правило брандмауэра).</summary>
    Network = 2,
}

/// <summary>Почему слушать нельзя. Код нужен экрану и журналу: текст меняется, код — нет.</summary>
public enum IngressDenyReason
{
    /// <summary>Запрета нет.</summary>
    None = 0,

    /// <summary>Прием выключен самим пользователем.</summary>
    Disabled,

    /// <summary>Сборка без слушателя: принимающего кода в ней нет по построению.</summary>
    NoListenerBuild,

    /// <summary>Сетевой режим не разрешен политикой организации.</summary>
    NetworkNotAllowed,

    /// <summary>Номер порта не годится.</summary>
    BadPort,
}

/// <summary>Решение двери: слушать или нет, почему и что показать человеку.</summary>
/// <param name="Allowed">Разрешено ли слушать.</param>
/// <param name="Reason">Код запрета; у разрешения — <see cref="IngressDenyReason.None"/>.</param>
/// <param name="Explanation">Текст для человека и для журнала.</param>
public sealed record IngressDecision(bool Allowed, IngressDenyReason Reason, string Explanation)
{
    private static readonly IngressDecision Ok = new(true, IngressDenyReason.None, "разрешено");

    public static IngressDecision Allow() => Ok;

    public static IngressDecision Deny(IngressDenyReason reason, string explanation) =>
        new(false, reason, explanation);
}

/// <summary>
/// ЕДИНСТВЕННАЯ дверь внутрь — зеркало <see cref="EgressGate"/>, и заведена она по той же
/// причине: на вопрос проверяющего «где программа слушает сеть» ответ обязан быть «один файл».
///
/// <b>Внутрь — только принимаем.</b> Ни одного обработчика, отдающего данные записей, за этой
/// дверью нет и не будет: токену-роботу по умолчанию выдается право писать и ни одного права
/// читать (10.12), и способ «вычитать базу наружу через свой же порт» не должен существовать
/// вовсе, а не быть выключен настройкой. Наружу отдается только описание собственной схемы —
/// имена коллекций и свойств, без значений.
///
/// Решения принимаются здесь, ввода-вывода нет: сокет открывает <c>IngressHttp</c>, а проверить
/// «что мы вообще разрешаем» можно без единого порта.
///
/// Разница с исходящей дверью одна, и она принципиальная: <b>петлевой адрес политики не
/// спрашивает</b>. Данные при приеме идут ВНУТРЬ, а обратиться к <c>127.0.0.1</c> может только
/// программа, уже работающая на этой машине, — то есть та, которая и так читает файл базы.
/// Сетевой режим — другое дело: слушать адрес машины разрешает организация, а не пользователь.
/// </summary>
public static class IngressGate
{
    /// <summary>Порт по умолчанию. Взят из плана (раздел 6.7) и ничем не занят в типовой машине.</summary>
    public const int DefaultPort = 8787;

    /// <summary>
    /// Границы номера порта. Ниже 1024 — системные порты: на Linux и macOS их занимает только
    /// суперпользователь, и предложить такой номер значило бы пообещать то, чего программа без
    /// прав администратора не сделает.
    /// </summary>
    public const int MinPort = 1024;

    /// <inheritdoc cref="MinPort"/>
    public const int MaxPort = 65535;

    /// <summary>Петлевой адрес. Записан строкой, а не именем <c>localhost</c>: имя резолвится
    /// в разное на разных машинах, и «слушаю там же, куда стучится 1С» перестало бы быть правдой.</summary>
    public const string LoopbackHost = "127.0.0.1";

    /// <summary>
    /// Потолок тела запроса. Тот же, что у файла в папке обмена: транспорта два, а правило одно —
    /// иначе сообщение, прошедшее папкой, отказывалось бы проходить по сети.
    /// </summary>
    public const long MaxBodyBytes = InboxFolder.MaxFileBytes;

    /// <summary>
    /// Есть ли в этой сборке принимающий код. Вторым флагом сборки (<c>-p:NoListener=true</c>)
    /// слушатель убирается целиком — на случай, если служба безопасности потребует машину без
    /// единого открытого порта (10.16.1).
    /// </summary>
    public static bool ListenerBuild =>
#if NO_LISTENER
        false;
#else
        true;
#endif

    /// <summary>
    /// Разрешено ли слушать так. Спрашивается и экраном (объяснить запрет до того, как человек
    /// начнет договариваться с отправителем), и самой службой перед открытием порта.
    /// </summary>
    public static IngressDecision Check(IngressMode mode, int port)
    {
        if (!ListenerBuild)
            return IngressDecision.Deny(IngressDenyReason.NoListenerBuild,
                "сборка без слушателя: принимать обращения по сети она не умеет");

        if (mode == IngressMode.Off)
            return IngressDecision.Deny(IngressDenyReason.Disabled, "прием по сети выключен");

        if (!ValidPort(port))
            return IngressDecision.Deny(IngressDenyReason.BadPort,
                $"номер порта годится от {MinPort} до {MaxPort}");

        if (mode == IngressMode.Network)
            return IngressDecision.Deny(IngressDenyReason.NetworkNotAllowed,
                "прием по локальной сети разрешает политика организации, а не пользователь");

        return IngressDecision.Allow();
    }

    /// <summary>Годится ли номер порта сам по себе — без вопроса о режиме.</summary>
    public static bool ValidPort(int port) => port is >= MinPort and <= MaxPort;

    /// <summary>
    /// Адрес, который называют отправителю. Пустая строка — слушать нечего: режим выключен или
    /// запрещен, и показывать адрес, по которому никто не ответит, значит объяснять отказ
    /// молчанием.
    /// </summary>
    public static string Address(IngressMode mode, int port) =>
        Check(mode, port).Allowed ? $"http://{LoopbackHost}:{port}" : "";
}
