using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SmartBase.Integrations;

/// <summary>
/// Мастер-пароль политики: им ответственный за интеграции подтверждает исходящее направление,
/// снимает блокировку и повышает квоту (docs/ROADMAP-API.md, 10.8).
///
/// <b>В программе его нет.</b> Хранится только проверочный хэш, и хранится он в САМОЙ ПОЛИТИКЕ,
/// которая приезжает извне. Поэтому подобрать пароль, правя базу или настройки, нельзя: базу
/// правит пользователь, а политику — нет.
///
/// <b>Чем защищен сам хэш</b> — зависит от источника политики (см. <see cref="PolicyLoader"/>):
/// ветку реестра <c>HKLM</c> и папку установки защищают права операционной системы, туда без
/// администратора машины не записать; папке раздачи, открытой на запись, нужна подпись
/// организации (<see cref="PolicySignature"/>), и без вшитого в сборку открытого ключа такой
/// источник не принимается вовсе.
///
/// PBKDF2 из состава .NET, соль своя у каждой политики. Ни своей криптографии, ни сторонней
/// библиотеки: пароль здесь защищает не тайну, а полномочие.
///
/// Формат строки: <c>pbkdf2$&lt;итерации&gt;$&lt;соль&gt;$&lt;хэш&gt;</c>, обе части в base64.
/// Отдельными полями он не хранится намеренно: политика — файл, который читают и глазами тоже, а
/// четыре поля вместо одного означают четыре повода разойтись.
/// </summary>
public static class MasterPassword
{
    /// <summary>
    /// Сколько итераций. Число записано в самой строке хэша, поэтому его можно поднять позже, не
    /// ломая старые политики: проверка читает то, что записано, а не то, что задано сейчас.
    /// </summary>
    public const int Iterations = 210_000;

    /// <summary>Длина соли и хэша в байтах.</summary>
    public const int SaltSize = 16;

    /// <inheritdoc cref="SaltSize"/>
    public const int HashSize = 32;

    private const string Prefix = "pbkdf2";

    /// <summary>
    /// Посчитать строку хэша для политики. Нужна тому, кто политику СОСТАВЛЯЕТ, и приходит к нему
    /// через окно «Мастер-пароль для политики» в контуре безопасности
    /// (<c>MasterHashDialog</c>) — оно единственный вызов из боевого кода.
    ///
    /// Долго это был вызов НИОТКУДА: формат свой, а посчитать строку было нечем, и составителю
    /// политики оставалось воспроизводить PBKDF2 с нашими параметрами сторонним скриптом. Без
    /// строки <c>masterHash</c> не подтверждается ни одно исходящее направление, то есть весь
    /// исходящий обмен упирался в число, которое программа считает, но не отдает.
    /// </summary>
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(password, salt, Iterations);

        return string.Join('$', Prefix,
                           Iterations.ToString(CultureInfo.InvariantCulture),
                           Convert.ToBase64String(salt),
                           Convert.ToBase64String(hash));
    }

    /// <summary>
    /// Совпадает ли пароль с записанным хэшем. <c>false</c> — не совпадает, строки хэша нет или
    /// она не разобралась.
    ///
    /// Испорченная строка считается НЕСОВПАДЕНИЕМ, а не отсутствием проверки: иначе достаточно
    /// было бы испортить одну букву в политике, чтобы подтверждать направления чем угодно.
    /// </summary>
    public static bool Matches(string? stored, string? password)
    {
        if (string.IsNullOrWhiteSpace(stored) || password is null) return false;

        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix) return false;

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture,
                          out var iterations) || iterations <= 0) return false;

        byte[] salt, expected;
        try
        {
            salt     = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length == 0 || expected.Length == 0) return false;

        // Сравнение постоянного времени: обычное «==» отвечает тем быстрее, чем раньше
        // разошлись байты, и по этому времени хэш подбирается по одному знаку.
        return CryptographicOperations.FixedTimeEquals(
            Derive(password, salt, iterations, expected.Length), expected);
    }

    /// <summary>Есть ли в строке вообще проверяемый хэш — не «правильный ли он».</summary>
    public static bool Defined(string? stored) =>
        !string.IsNullOrWhiteSpace(stored)
        && stored.Split('$') is [Prefix, _, _, _];

    private static byte[] Derive(string password, byte[] salt, int iterations, int size = HashSize) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations,
                                  HashAlgorithmName.SHA256, size);
}
