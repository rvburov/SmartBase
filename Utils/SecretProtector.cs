using System.Runtime.InteropServices;
using Serilog;

namespace SmartBase.Utils;

/// <summary>
/// Защищенное хранение секретов (паролей PG-подключений) средствами ОС.
/// В app.db вместо пароля лежит непрозрачная строка с префиксом:
///   «keychain:v1:{id}» — macOS: сам пароль в Связке ключей (служба
///     «SmartBase PostgreSQL», учетная запись = id); копия app.db на другую машину
///     пароль не переносит — база попросит переподключение;
///   «dpapi:v1:{base64}» — Windows: DPAPI, ключ пользователя (CurrentUser) —
///     расшифровать может только тот же пользователь Windows;
///   без префикса — открытый текст: Linux (системного хранилища не гарантируется)
///     и запасной путь при недоступном хранилище — уровень защиты прежней v1
///     (права доступа файла app.db).
/// Пустой пароль не защищается (нечего). Потерянный элемент Связки ключей дает
/// пустой пароль — сервер ответит «неверный пароль», пользователь переподключит базу.
/// Примечание для разработки на macOS: у dev-сборок меняется бинарник, и Связка
/// ключей может переспрашивать доступ к старым элементам («Разрешить») — у
/// подписанных релизов такого нет.
/// </summary>
public static class SecretProtector
{
    private const string KeychainPrefix  = "keychain:v1:";
    private const string DpapiPrefix     = "dpapi:v1:";
    // Имя службы в Связке ключей macOS. Историческое: сначала защищались только пароли
    // PostgreSQL. Переименовать нельзя — по этому имени лежат уже сохраненные элементы, и
    // смена имени сделала бы их ненайденными. Новым потребителям — своя служба параметром.
    private const string KeychainService = "SmartBase PostgreSQL";

    /// <summary>
    /// Служба Связки ключей для секретов интеграций (токены и пароли чужих программ).
    /// Отдельная от паролей баз намеренно: удаление базы не должно задевать токен обмена,
    /// а человеку в Связке ключей видно, что к чему относится.
    /// </summary>
    public const string IntegrationsService = "SmartBase Integrations";

    /// <summary>Строка уже в защищенном виде (имеет известный префикс)?</summary>
    public static bool IsProtected(string stored) =>
        stored.StartsWith(KeychainPrefix, StringComparison.Ordinal) ||
        stored.StartsWith(DpapiPrefix, StringComparison.Ordinal);

    /// <summary>Секрет → строка для хранения. При недоступном хранилище ОС — как есть.</summary>
    public static string Protect(string secret, string service = KeychainService)
    {
        if (secret.Length == 0) return secret;
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var account = Guid.NewGuid().ToString("N");
                MacKeychain.Save(service, account, secret);
                return KeychainPrefix + account;
            }
            if (OperatingSystem.IsWindows())
            {
                var bytes = System.Security.Cryptography.ProtectedData.Protect(
                    System.Text.Encoding.UTF8.GetBytes(secret),
                    optionalEntropy: null,
                    scope: System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return DpapiPrefix + Convert.ToBase64String(bytes);
            }
        }
        catch (Exception ex)
        {
            // Хранилище ОС недоступно (заблокированная связка, политика домена…) —
            // не валим подключение базы, храним по-старому. Сам секрет НЕ логируем.
            Log.Warning(ex, "Хранилище ОС недоступно — пароль сохранен без защиты");
        }
        return secret;
    }

    /// <summary>Строка из хранения → секрет. Утерянный/нечитаемый секрет — пустая строка.</summary>
    public static string Unprotect(string stored, string service = KeychainService)
    {
        if (stored.StartsWith(KeychainPrefix, StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsMacOS()) return "";
            try { return MacKeychain.Load(service, stored[KeychainPrefix.Length..]) ?? ""; }
            catch (Exception ex) { Log.Warning(ex, "Не удалось прочитать пароль из Связки ключей"); return ""; }
        }
        if (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows()) return "";
            try
            {
                var bytes = System.Security.Cryptography.ProtectedData.Unprotect(
                    Convert.FromBase64String(stored[DpapiPrefix.Length..]),
                    optionalEntropy: null,
                    scope: System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex) { Log.Warning(ex, "Не удалось расшифровать пароль (DPAPI)"); return ""; }
        }
        return stored;   // открытый текст (legacy / Linux)
    }

    /// <summary>Удалить секрет из хранилища ОС (при удалении базы из реестра).</summary>
    public static void Delete(string stored, string service = KeychainService)
    {
        if (!stored.StartsWith(KeychainPrefix, StringComparison.Ordinal)) return;   // DPAPI — без внешнего состояния
        if (!OperatingSystem.IsMacOS()) return;
        try { MacKeychain.Delete(service, stored[KeychainPrefix.Length..]); }
        catch { /* нет элемента — и не надо */ }
    }

    // ── macOS: generic password в Связке ключей через Security.framework ─────────
    // P/Invoke вместо пакета: официального .NET-биндинга Keychain нет, а нужен
    // ровно один тип элемента (kSecClassGenericPassword) и три операции.
    private static class MacKeychain
    {
        private const string SecLib = "/System/Library/Frameworks/Security.framework/Security";
        private const string CfLib  = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        private const int  ErrSecItemNotFound     = -25300;
        private const uint KCfStringEncodingUtf8  = 0x08000100;

        [DllImport(SecLib)] private static extern int SecItemAdd(IntPtr attrs, IntPtr result);
        [DllImport(SecLib)] private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
        [DllImport(SecLib)] private static extern int SecItemDelete(IntPtr query);

        [DllImport(CfLib)] private static extern IntPtr CFStringCreateWithBytes(
            IntPtr alloc, byte[] bytes, long numBytes, uint encoding, byte isExternalRepresentation);
        [DllImport(CfLib)] private static extern IntPtr CFDataCreate(IntPtr alloc, byte[] bytes, long length);
        [DllImport(CfLib)] private static extern IntPtr CFDictionaryCreate(
            IntPtr alloc, IntPtr[] keys, IntPtr[] values, long numValues,
            IntPtr keyCallBacks, IntPtr valueCallBacks);
        [DllImport(CfLib)] private static extern void CFRelease(IntPtr cf);
        [DllImport(CfLib)] private static extern long CFDataGetLength(IntPtr data);
        [DllImport(CfLib)] private static extern IntPtr CFDataGetBytePtr(IntPtr data);

        [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlopen(string path, int mode);
        [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlsym(IntPtr handle, string symbol);

        // Экспортированные константы фреймворков: kSec*/kCFBooleanTrue — глобальные
        // CFStringRef/CFBooleanRef (читаем значение указателя), kCFTypeDictionary*CallBacks —
        // глобальные структуры (нужен сам адрес).
        private static readonly IntPtr SecHandle = dlopen(SecLib, 2 /* RTLD_NOW */);
        private static readonly IntPtr CfHandle  = dlopen(CfLib, 2);

        private static IntPtr SecConst(string name) => Marshal.ReadIntPtr(dlsym(SecHandle, name));
        private static IntPtr CfConst(string name)  => Marshal.ReadIntPtr(dlsym(CfHandle, name));

        private static readonly IntPtr KSecClass                = SecConst("kSecClass");
        private static readonly IntPtr KSecClassGenericPassword = SecConst("kSecClassGenericPassword");
        private static readonly IntPtr KSecAttrService          = SecConst("kSecAttrService");
        private static readonly IntPtr KSecAttrAccount          = SecConst("kSecAttrAccount");
        private static readonly IntPtr KSecValueData            = SecConst("kSecValueData");
        private static readonly IntPtr KSecReturnData           = SecConst("kSecReturnData");
        private static readonly IntPtr KSecMatchLimit           = SecConst("kSecMatchLimit");
        private static readonly IntPtr KSecMatchLimitOne        = SecConst("kSecMatchLimitOne");
        private static readonly IntPtr KCfBooleanTrue           = CfConst("kCFBooleanTrue");
        private static readonly IntPtr KeyCallbacks             = dlsym(CfHandle, "kCFTypeDictionaryKeyCallBacks");
        private static readonly IntPtr ValueCallbacks           = dlsym(CfHandle, "kCFTypeDictionaryValueCallBacks");

        public static void Save(string service, string account, string secret)
        {
            Delete(service, account);   // перезапись: тот же id не должен дать duplicate

            var cfService = CfString(service);
            var cfAccount = CfString(account);
            var secretBytes = System.Text.Encoding.UTF8.GetBytes(secret);
            var cfData = CFDataCreate(IntPtr.Zero, secretBytes, secretBytes.Length);
            var attrs = Dict(
                [KSecClass, KSecAttrService, KSecAttrAccount, KSecValueData],
                [KSecClassGenericPassword, cfService, cfAccount, cfData]);
            try
            {
                var status = SecItemAdd(attrs, IntPtr.Zero);
                if (status != 0)
                    throw new InvalidOperationException($"SecItemAdd: {status}");
            }
            finally
            {
                CFRelease(attrs); CFRelease(cfData); CFRelease(cfAccount); CFRelease(cfService);
            }
        }

        public static string? Load(string service, string account)
        {
            var cfService = CfString(service);
            var cfAccount = CfString(account);
            var query = Dict(
                [KSecClass, KSecAttrService, KSecAttrAccount, KSecReturnData, KSecMatchLimit],
                [KSecClassGenericPassword, cfService, cfAccount, KCfBooleanTrue, KSecMatchLimitOne]);
            try
            {
                var status = SecItemCopyMatching(query, out var result);
                if (status == ErrSecItemNotFound) return null;
                if (status != 0)
                    throw new InvalidOperationException($"SecItemCopyMatching: {status}");
                try
                {
                    var length = (int)CFDataGetLength(result);
                    var bytes  = new byte[length];
                    Marshal.Copy(CFDataGetBytePtr(result), bytes, 0, length);
                    return System.Text.Encoding.UTF8.GetString(bytes);
                }
                finally { CFRelease(result); }
            }
            finally
            {
                CFRelease(query); CFRelease(cfAccount); CFRelease(cfService);
            }
        }

        public static void Delete(string service, string account)
        {
            var cfService = CfString(service);
            var cfAccount = CfString(account);
            var query = Dict(
                [KSecClass, KSecAttrService, KSecAttrAccount],
                [KSecClassGenericPassword, cfService, cfAccount]);
            try { SecItemDelete(query); }   // ItemNotFound — нормальный исход
            finally
            {
                CFRelease(query); CFRelease(cfAccount); CFRelease(cfService);
            }
        }

        private static IntPtr CfString(string s)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(s);
            return CFStringCreateWithBytes(IntPtr.Zero, bytes, bytes.Length, KCfStringEncodingUtf8, 0);
        }

        private static IntPtr Dict(IntPtr[] keys, IntPtr[] values) =>
            CFDictionaryCreate(IntPtr.Zero, keys, values, keys.Length, KeyCallbacks, ValueCallbacks);
    }
}
