using System.Text;

namespace SmartBase.Integrations;

/// <summary>
/// Разобранная строка cURL: заготовка соединения и заготовка запроса. Пользователь копирует
/// строку из чужой документации, программа ее разбирает — обмен настроен. Это самый короткий
/// путь к «настрой сам», сильнее любых пресетов (docs/ROADMAP-API.md, раздел 5).
///
/// Секрет вынут из строки отдельным полем и в заголовках его уже нет. Иначе он лег бы открытым
/// текстом в <c>app.db</c>, а потом попал бы в журнал запусков — а строка из документации почти
/// всегда содержит именно секрет.
/// </summary>
/// <param name="Address">Базовый адрес для соединения: <c>https://сервер:порт</c>.</param>
/// <param name="Path">Путь с параметрами для запроса: <c>/orders?since=…</c>.</param>
/// <param name="Method">Метод.</param>
/// <param name="Headers">Заголовки без заголовка авторизации и без служебных.</param>
/// <param name="Body">Тело запроса.</param>
/// <param name="Auth">Опознанный способ авторизации.</param>
/// <param name="AuthName">Имя заголовка, параметра или пользователя.</param>
/// <param name="Secret">Вынутый секрет; пусто — авторизации в строке не было.</param>
/// <param name="TimeoutSeconds">Таймаут из <c>--max-time</c>; <c>null</c> — не указан.</param>
/// <param name="Notes">Что не разобралось или было изменено. Показывается человеку: он должен
/// видеть, что программа сделала со строкой, а не гадать.</param>
public sealed record CurlImport(
    string                           Address,
    string                           Path,
    RequestMethod                    Method,
    IReadOnlyList<IntegrationHeader> Headers,
    string                           Body,
    ConnectionAuth                   Auth,
    string                           AuthName,
    string                           Secret,
    int?                             TimeoutSeconds,
    IReadOnlyList<string>            Notes)
{
    /// <summary>Заготовка соединения: адрес, способ авторизации, постоянные заголовки.</summary>
    public IntegrationConnection ToConnection(string name) =>
        IntegrationConnection.New() with
        {
            Name           = name,
            Address        = Address,
            Auth           = Auth,
            AuthName       = AuthName,
            TimeoutSeconds = TimeoutSeconds ?? IntegrationConnection.DefaultTimeoutSeconds,
        };

    /// <summary>
    /// Заготовка запроса. Заголовки из строки кладутся сюда, а не в соединение: одна строка
    /// cURL описывает один вызов, и объявлять ее заголовки общими для всех будущих запросов
    /// было бы домыслом.
    /// </summary>
    public IntegrationRequest ToRequest(string name, int connectionId) =>
        IntegrationRequest.New() with
        {
            Name         = name,
            ConnectionId = connectionId,
            Method       = Method,
            Path         = Path,
            Headers      = Headers,
            Body         = Body,
        };
}

/// <summary>
/// Разбор строки cURL. Чистый текст: ничего не запускается, файлы не читаются, сеть не
/// трогается. Это важно — строку приносят извне, и «выполнить, чтобы понять» здесь означало бы
/// выполнить чужую команду.
///
/// Разбирается то, что встречается в документации к обмену: адрес, метод, заголовки, тело,
/// <c>-u</c>, <c>-G</c>, <c>--max-time</c>. Все прочее не роняет разбор, а попадает в
/// примечания: половина настроенного запроса лучше отказа со словом «непонятно».
/// </summary>
public static class CurlParser
{
    /// <summary>
    /// Имена заголовков, значение которых считается секретом и выносится из карточки. Список
    /// закрытый: угадывать по виду значения нельзя — под угадывание попадет обычный параметр,
    /// и запрос молча изменится.
    /// </summary>
    private static readonly string[] SecretHeaders =
        ["x-api-key", "api-key", "apikey", "x-auth-token", "x-access-token", "x-token", "private-token"];

    /// <summary>Имена параметров адреса, значение которых считается секретом.</summary>
    private static readonly string[] SecretQueryNames =
        ["api_key", "apikey", "access_token", "token", "auth_token", "key"];

    /// <summary>Заголовки, которые считает и подставит сам транспорт.</summary>
    private static readonly string[] TransportHeaders =
        ["host", "content-length", "connection", "transfer-encoding"];

    /// <summary>Короткие ключи, у которых есть значение.</summary>
    private const string ShortWithValue = "XHdubAeoFKTmx";

    /// <summary>Короткие ключи без значения; молча пропускаются.</summary>
    private const string ShortWithoutValue = "sSLkvifgN0146#";

    /// <summary>
    /// Разобрать строку. <c>false</c> — строка не похожа на cURL или в ней нет адреса; причина
    /// в <paramref name="error"/> написана человеческим языком, ее показывают в окне вставки.
    /// </summary>
    public static bool TryParse(string text, out CurlImport? result, out string error)
    {
        result = null;

        if (!TryTokenize(text ?? "", out var tokens, out error))
            return false;

        if (tokens.Count == 0)
        {
            error = "Пусто: вставьте строку вида curl https://сервер/путь";
            return false;
        }

        // Приглашение оболочки, если строку скопировали вместе с ним.
        if (tokens[0] is "$" or ">" or "PS>")
            tokens.RemoveAt(0);

        if (tokens.Count == 0 || !LooksLikeCurl(tokens[0]))
        {
            error = "Строка должна начинаться со слова curl";
            return false;
        }

        var state = new ParseState();

        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (token.StartsWith("--", StringComparison.Ordinal))
                ReadLong(token, tokens, ref i, state);
            else if (token.Length > 1 && token[0] == '-')
                ReadShort(token, tokens, ref i, state);
            else
                ReadBare(token, state);
        }

        return Build(state, out result, out error);
    }

    private static bool LooksLikeCurl(string token)
    {
        var name = token.Trim('"', '\'');
        var slash = name.LastIndexOfAny(['/', '\\']);
        if (slash >= 0) name = name[(slash + 1)..];

        return name.Equals("curl", StringComparison.OrdinalIgnoreCase)
            || name.Equals("curl.exe", StringComparison.OrdinalIgnoreCase);
    }

    // ── Разбор ключей ─────────────────────────────────────────────────────────

    /// <summary>Копилка разобранного: заполняется по ходу, проверяется в конце.</summary>
    private sealed class ParseState
    {
        public string?             Url;
        public string?             Method;
        public readonly List<IntegrationHeader> Headers = [];
        public readonly List<string> Data = [];
        public string?             User;
        public bool                DataAsQuery;
        public int?                Timeout;
        public readonly List<string> Notes = [];

        public void Note(string note)
        {
            if (!Notes.Contains(note)) Notes.Add(note);
        }
    }

    /// <summary>Длинный ключ: <c>--header</c>, <c>--data</c> и прочие.</summary>
    private static void ReadLong(string token, List<string> tokens, ref int i, ParseState state)
    {
        // Форма «--header=значение» встречается наравне с «--header значение».
        var equals = token.IndexOf('=');
        var name   = equals > 0 ? token[..equals] : token;
        var inline = equals > 0 ? token[(equals + 1)..] : null;

        switch (name)
        {
            case "--request":       state.Method = Value(inline, tokens, ref i)?.ToUpperInvariant(); break;
            case "--url":           state.Url    = Value(inline, tokens, ref i);                     break;
            case "--header":        AddHeader(Value(inline, tokens, ref i), state);                  break;
            case "--user":          state.User   = Value(inline, tokens, ref i);                     break;
            case "--get":           state.DataAsQuery = true;                                        break;

            case "--data" or "--data-raw" or "--data-ascii" or "--data-binary":
                AddData(Value(inline, tokens, ref i), state);
                break;

            case "--data-urlencode":
                AddData(UrlEncode(Value(inline, tokens, ref i)), state);
                break;

            case "--json":
                AddData(Value(inline, tokens, ref i), state);
                AddHeaderIfAbsent("Content-Type", "application/json", state);
                AddHeaderIfAbsent("Accept",       "application/json", state);
                break;

            case "--oauth2-bearer":
                AddHeader("Authorization: Bearer " + Value(inline, tokens, ref i), state);
                break;

            case "--user-agent": AddHeader("User-Agent: " + Value(inline, tokens, ref i), state); break;
            case "--cookie":     AddHeader("Cookie: "     + Value(inline, tokens, ref i), state); break;
            case "--referer":    AddHeader("Referer: "    + Value(inline, tokens, ref i), state); break;

            case "--max-time" or "--connect-timeout":
                if (int.TryParse(Value(inline, tokens, ref i), out var seconds) && seconds > 0)
                    state.Timeout = seconds;
                break;

            case "--form" or "--form-string":
                Value(inline, tokens, ref i);
                state.Note("Многочастная форма (--form) не поддерживается: заполните тело вручную");
                break;

            case "--config":
                Value(inline, tokens, ref i);
                state.Note("Чтение файла настроек (--config) не поддерживается");
                break;

            case "--insecure":
                state.Note("Отключение проверки сертификата (--insecure) не переносится: "
                         + "программа всегда проверяет сертификат");
                break;

            // Ключи вывода и поведения оболочки к запросу отношения не имеют.
            case "--silent" or "--show-error" or "--verbose" or "--compressed" or "--include"
              or "--fail" or "--globoff" or "--no-buffer" or "--location" or "--http1.1" or "--http2":
                break;

            case "--output" or "--upload-file" or "--proxy" or "--cert" or "--key" or "--cacert"
              or "--retry" or "--write-out":
                Value(inline, tokens, ref i);
                state.Note($"Параметр {name} пропущен");
                break;

            case "--head":
                state.Note("Метод HEAD не поддерживается: взят GET");
                break;

            default:
                state.Note($"Параметр {name} не разобран");
                break;
        }
    }

    /// <summary>
    /// Короткий ключ. cURL разрешает и «-X POST», и «-XPOST», и слипшиеся «-sSL» — разбираются
    /// все три вида, иначе половина строк из документации отвалится.
    /// </summary>
    private static void ReadShort(string token, List<string> tokens, ref int i, ParseState state)
    {
        for (var c = 1; c < token.Length; c++)
        {
            var key = token[c];

            if (ShortWithoutValue.Contains(key)) continue;

            if (key == 'G') { state.DataAsQuery = true; continue; }
            if (key == 'I') { state.Note("Метод HEAD не поддерживается: взят GET"); continue; }

            if (!ShortWithValue.Contains(key))
            {
                state.Note($"Ключ -{key} не разобран");
                continue;
            }

            // Значение — либо остаток этого же токена, либо следующий токен.
            var rest  = token[(c + 1)..];
            var value = rest.Length > 0 ? rest : Next(tokens, ref i);

            switch (key)
            {
                case 'X': state.Method = value?.ToUpperInvariant(); break;
                case 'H': AddHeader(value, state);                  break;
                case 'd': AddData(value, state);                    break;
                case 'u': state.User = value;                       break;
                case 'A': AddHeader("User-Agent: " + value, state);  break;
                case 'b': AddHeader("Cookie: "     + value, state);  break;
                case 'e': AddHeader("Referer: "    + value, state);  break;

                case 'm':
                    if (int.TryParse(value, out var seconds) && seconds > 0) state.Timeout = seconds;
                    break;

                case 'F':
                    state.Note("Многочастная форма (-F) не поддерживается: заполните тело вручную");
                    break;

                default:
                    state.Note($"Ключ -{key} пропущен");
                    break;
            }

            return;   // значение съело остаток токена
        }
    }

    /// <summary>Голый фрагмент: это адрес, если адреса еще нет.</summary>
    private static void ReadBare(string token, ParseState state)
    {
        if (state.Url is null) state.Url = token;
        else state.Note($"Лишний фрагмент «{token}» пропущен");
    }

    private static string? Value(string? inline, List<string> tokens, ref int i) =>
        inline ?? Next(tokens, ref i);

    private static string? Next(List<string> tokens, ref int i) =>
        i + 1 < tokens.Count ? tokens[++i] : null;

    private static void AddHeader(string? raw, ParseState state)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;

        var colon = raw.IndexOf(':');
        if (colon <= 0)
        {
            state.Note($"Заголовок «{raw.Trim()}» не разобран: ожидалось «Имя: значение»");
            return;
        }

        var name  = raw[..colon].Trim();
        var value = raw[(colon + 1)..].Trim();

        state.Headers.RemoveAll(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        state.Headers.Add(new IntegrationHeader(name, value));
    }

    /// <summary>Добавить заголовок, не затирая заданный человеком: «--json» лишь дополняет строку.</summary>
    private static void AddHeaderIfAbsent(string name, string value, ParseState state)
    {
        if (state.Headers.Any(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;

        state.Headers.Add(new IntegrationHeader(name, value));
    }

    /// <summary>
    /// Тело. «-d @файл» не читается сознательно: строку принесли снаружи, и лезть по ней в
    /// файловую систему нельзя.
    /// </summary>
    private static void AddData(string? value, ParseState state)
    {
        if (string.IsNullOrEmpty(value)) return;

        if (value.StartsWith('@'))
        {
            state.Note("Тело из файла (-d @файл) не подставлено: укажите его вручную");
            return;
        }

        state.Data.Add(value);
    }

    private static string? UrlEncode(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        var equals = value.IndexOf('=');
        return equals <= 0
            ? Uri.EscapeDataString(value)
            : value[..(equals + 1)] + Uri.EscapeDataString(value[(equals + 1)..]);
    }

    // ── Сборка ────────────────────────────────────────────────────────────────

    private static bool Build(ParseState state, out CurlImport? result, out string error)
    {
        result = null;
        error  = "";

        if (string.IsNullOrWhiteSpace(state.Url))
        {
            error = "В строке нет адреса";
            return false;
        }

        var raw = state.Url!.Trim();

        // Схемы нет — берем https. Догадка в пользу защищенного канала, и о ней сказано вслух.
        if (!raw.Contains("://", StringComparison.Ordinal))
        {
            raw = "https://" + raw;
            state.Note("Схема не указана — взят https");
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var url))
        {
            error = $"Адрес «{state.Url}» не разобрался";
            return false;
        }

        if (url.Scheme is not ("http" or "https"))
        {
            error = $"Схема «{url.Scheme}» не поддерживается: разрешены только http и https";
            return false;
        }

        if (!TryMethod(state, out var method, out error)) return false;

        var body = string.Join("&", state.Data);
        var path = url.PathAndQuery;

        // «-G» переносит данные в адрес: так их пишут для GET-запросов с длинным списком
        // параметров.
        if (state.DataAsQuery && body.Length > 0)
        {
            path += (path.Contains('?') ? "&" : "?") + body;
            body  = "";
        }

        if (body.Length > 0 && state.Method is null) method = RequestMethod.Post;

        if (body.Length > 0 && method == RequestMethod.Get)
        {
            state.Note("У метода GET тела не бывает — тело перенесено в адрес");
            path += (path.Contains('?') ? "&" : "?") + body;
            body  = "";
        }

        var (auth, authName, secret) = ExtractSecret(state, ref path);

        var headers = state.Headers
            .Where(h => !TransportHeaders.Contains(h.Name.ToLowerInvariant()))
            .ToList();

        var address = url.GetLeftPart(UriPartial.Authority);

        result = new CurlImport(
            address, path, method, headers, body,
            auth, authName, secret, state.Timeout, state.Notes);

        return true;
    }

    private static bool TryMethod(ParseState state, out RequestMethod method, out string error)
    {
        method = RequestMethod.Get;
        error  = "";

        if (state.Method is null) return true;

        switch (state.Method)
        {
            case "GET":    method = RequestMethod.Get;    return true;
            case "POST":   method = RequestMethod.Post;   return true;
            case "PUT":    method = RequestMethod.Put;    return true;
            case "PATCH":  method = RequestMethod.Patch;  return true;
            case "DELETE": method = RequestMethod.Delete; return true;

            default:
                error = $"Метод {state.Method} не поддерживается";
                return false;
        }
    }

    /// <summary>
    /// Вынуть секрет из заголовков и адреса. Всегда с примечанием: человек должен видеть, что
    /// именно программа сочла секретом, — молчаливый перенос параметра из адреса в поле
    /// авторизации выглядел бы как потеря параметра.
    /// </summary>
    private static (ConnectionAuth Auth, string Name, string Secret) ExtractSecret(
        ParseState state, ref string path)
    {
        // 1. Заголовок Authorization — самый частый случай.
        var authorization = state.Headers
            .FirstOrDefault(h => h.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase));

        if (authorization is not null)
        {
            state.Headers.Remove(authorization);
            var value = authorization.Value.Trim();

            if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                state.Note("Токен из заголовка Authorization сохранен отдельно");
                return (ConnectionAuth.Bearer, "", value[7..].Trim());
            }

            if (value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                var (user, password) = DecodeBasic(value[6..].Trim());
                state.Note("Имя и пароль из заголовка Authorization сохранены отдельно");
                return (ConnectionAuth.Basic, user, password);
            }

            state.Note("Значение заголовка Authorization сохранено отдельно");
            return (ConnectionAuth.Header, authorization.Name, value);
        }

        // 2. Ключ «-u имя:пароль».
        if (!string.IsNullOrEmpty(state.User))
        {
            var colon = state.User!.IndexOf(':');
            state.Note("Имя и пароль из -u сохранены отдельно");

            return colon < 0
                ? (ConnectionAuth.Basic, state.User, "")
                : (ConnectionAuth.Basic, state.User[..colon], state.User[(colon + 1)..]);
        }

        // 3. Известный заголовок с ключом.
        var keyHeader = state.Headers
            .FirstOrDefault(h => SecretHeaders.Contains(h.Name.ToLowerInvariant()));

        if (keyHeader is not null)
        {
            state.Headers.Remove(keyHeader);
            state.Note($"Ключ из заголовка {keyHeader.Name} сохранен отдельно");
            return (ConnectionAuth.Header, keyHeader.Name, keyHeader.Value);
        }

        // 4. Ключ в адресе. Опаснее прочих: без выноса он лег бы в журнал запусков вместе с
        //    адресом, и журнал стал бы местом утечки токена.
        if (TryTakeQuerySecret(ref path, out var queryName, out var querySecret))
        {
            state.Note($"Параметр {queryName} убран из адреса и сохранен отдельно");
            return (ConnectionAuth.Query, queryName, querySecret);
        }

        return (ConnectionAuth.None, "", "");
    }

    private static (string User, string Password) DecodeBasic(string encoded)
    {
        try
        {
            var plain = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var colon = plain.IndexOf(':');
            return colon < 0 ? (plain, "") : (plain[..colon], plain[(colon + 1)..]);
        }
        catch (FormatException)
        {
            return ("", encoded);
        }
    }

    private static bool TryTakeQuerySecret(ref string path, out string name, out string secret)
    {
        name   = "";
        secret = "";

        var mark = path.IndexOf('?');
        if (mark < 0) return false;

        var head  = path[..mark];
        var parts = path[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries).ToList();

        for (var i = 0; i < parts.Count; i++)
        {
            var equals = parts[i].IndexOf('=');
            if (equals <= 0) continue;

            var candidate = parts[i][..equals];
            if (!SecretQueryNames.Contains(candidate.ToLowerInvariant())) continue;

            name   = candidate;
            secret = Uri.UnescapeDataString(parts[i][(equals + 1)..]);
            parts.RemoveAt(i);

            path = parts.Count == 0 ? head : head + "?" + string.Join("&", parts);
            return true;
        }

        return false;
    }

    // ── Разбор строки на фрагменты ────────────────────────────────────────────

    /// <summary>
    /// Разложить строку на фрагменты по правилам оболочки: одинарные кавычки — как есть,
    /// двойные — с экранированием, «\», «^» и «`» в конце строки — перенос. Три вида переноса
    /// потому, что строку копируют из bash, из cmd и из PowerShell, и человеку все равно, какая
    /// из оболочек породила ту, что у него в буфере.
    /// </summary>
    private static bool TryTokenize(string text, out List<string> tokens, out string error)
    {
        tokens = [];
        error  = "";

        var current = new StringBuilder();
        var started = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c is '\\' or '^' or '`' && IsLineEnd(text, i + 1))
            {
                i = SkipLineEnd(text, i + 1) - 1;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (started) { tokens.Add(current.ToString()); current.Clear(); started = false; }
                continue;
            }

            started = true;

            if (c == '\'')
            {
                var close = text.IndexOf('\'', i + 1);
                if (close < 0) { error = "Не закрыта одинарная кавычка"; return false; }

                current.Append(text, i + 1, close - i - 1);
                i = close;
                continue;
            }

            if (c == '"')
            {
                if (!ReadDoubleQuoted(text, ref i, current)) { error = "Не закрыта двойная кавычка"; return false; }
                continue;
            }

            if (c == '\\' && i + 1 < text.Length)
            {
                current.Append(text[++i]);
                continue;
            }

            current.Append(c);
        }

        if (started) tokens.Add(current.ToString());
        return true;
    }

    private static bool ReadDoubleQuoted(string text, ref int i, StringBuilder current)
    {
        for (var j = i + 1; j < text.Length; j++)
        {
            var c = text[j];

            if (c == '\\' && j + 1 < text.Length)
            {
                var next = text[j + 1];

                // В двойных кавычках оболочка снимает «\» только перед немногими знаками;
                // «\n» внутри JSON должен дожить до тела запроса неизменным.
                if (next is '"' or '\\' or '$' or '`') { current.Append(next); j++; continue; }

                current.Append(c);
                continue;
            }

            if (c == '"') { i = j; return true; }

            current.Append(c);
        }

        return false;
    }

    private static bool IsLineEnd(string text, int i) =>
        i < text.Length && (text[i] == '\n' || text[i] == '\r');

    private static int SkipLineEnd(string text, int i)
    {
        while (i < text.Length && (text[i] == '\r' || text[i] == '\n')) i++;
        return i;
    }
}
