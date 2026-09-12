using System.Net;
using System.Text;

namespace SmartBase.Integrations;

/// <summary>
/// Слушающий слой — ЕДИНСТВЕННОЕ место в решении, где открывается порт.
///
/// Зеркало отправляющего слоя <c>EgressHttp.Open.cs</c> и сделано по тем же правилам: здесь нет
/// ни одного решения о том, кого пускать, — только чтение запроса и запись ответа. Кого пускать,
/// решает <c>IngressHandler</c>, и проверить это можно без единого сокета.
///
/// Отсюда и имя файла: вторым флагом сборки (<c>-p:NoListener=true</c>) он заменяется заглушкой
/// <c>IngressHttp.Closed.cs</c>, и в дистрибутиве не остается принимающего кода вовсе — так же,
/// как в закрытой сборке не остается отправляющего (docs/ROADMAP-API.md, 10.16.1).
///
/// <b>Обращения обрабатываются по одному.</b> Не от лени: канал местный, обращений тут единицы в
/// минуту, а последовательная обработка делает проверку повтора честной — два одинаковых
/// сообщения, пришедших разом, не разойдутся по разным потокам и не заведут записи дважды.
///
/// <c>HttpListener</c> из состава .NET: Kestrel ради трех адресов в настольное приложение не
/// тянут (раздел 7). Петлевой адрес <c>http://127.0.0.1:порт/</c> открывается БЕЗ прав
/// администратора — в этом и смысл первого шага.
/// </summary>
//
// Запрет сетевых типов снят НА ВЕСЬ ФАЙЛ — тем же приемом и по той же причине, что у
// отправляющего слоя: файл и есть то единственное место, где слушателю позволено быть
// (BannedSymbols.txt, правило RS0030). Снятие пофразно говорило бы, что дверей много и каждая
// маленькая. Мест с этой строкой в репозитории три, и третье — вот оно; появление четвертого
// роняет тест OneDoorOutwardTests.Suppressions_live_only_at_the_two_doors_and_the_postgres_probe.
#pragma warning disable RS0030
public sealed class IngressHttp : IDisposable
{
    private readonly HttpListener _listener;

    private readonly Func<IngressCall, IngressAnswer> _answer;
    private readonly CancellationTokenSource          _stop = new();

    private IngressHttp(HttpListener listener, Func<IngressCall, IngressAnswer> answer)
    {
        _listener = listener;
        _answer   = answer;
    }

    /// <summary>
    /// Открыть порт. <c>null</c> — не открылся, причина в <paramref name="trouble"/>: занятый
    /// порт и отсутствие прав на адрес — обычные беды боевой машины, и падать из-за них
    /// программа не должна.
    /// </summary>
    /// <param name="prefix">Адрес вида <c>http://127.0.0.1:8787/</c> — с косой чертой на конце,
    /// иначе <c>HttpListener</c> его не принимает.</param>
    /// <param name="answer">Кто отвечает на обращения.</param>
    /// <param name="trouble">Почему не открылся; пусто — открылся.</param>
    public static IngressHttp? TryOpen(string prefix, Func<IngressCall, IngressAnswer> answer,
                                       out string trouble)
    {
        trouble = "";

        var listener = new HttpListener();

        listener.Prefixes.Add(prefix);

        try
        {
            listener.Start();
        }
        catch (Exception failure) when (failure is HttpListenerException
                                                or PlatformNotSupportedException
                                                or ObjectDisposedException)
        {
            trouble = failure.Message;
            ((IDisposable)listener).Dispose();
            return null;
        }

        var door = new IngressHttp(listener, answer);
        _ = Task.Run(door.LoopAsync);

        return door;
    }

    /// <summary>Закрыть порт. Повторный вызов безвреден.</summary>
    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;

        _stop.Cancel();

        try { _listener.Stop(); }   finally { ((IDisposable)_listener).Dispose(); }

        _stop.Dispose();
    }

    // ── Прием ─────────────────────────────────────────────────────────────────

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;   // закрылись — это не беда, а выход
            }
            catch (Exception failure)
            {
                // Слушатель упал сам по себе. Молчать нельзя: порт после этого закрыт, и обмен
                // встанет, а отправитель будет думать, что программа его игнорирует.
                Serilog.Log.Warning("Прием по сети: слушатель остановлен — {Reason:l}",
                                    failure.Message);
                return;
            }

            try
            {
                await ServeAsync(context);
            }
            catch (Exception failure)
            {
                // Одно обращение не должно уносить слушателя: следующее может быть настроено
                // правильно, а закрывшийся порт починит только перезапуск программы.
                Serilog.Log.Warning(failure, "Прием по сети: обращение не обработалось");
            }
        }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        var request = context.Request;

        string body;
        try
        {
            body = await ReadBodyAsync(request);
        }
        catch (InvalidDataException oversize)
        {
            await WriteAsync(context, IngressAnswer.Problem(413, oversize.Message));
            return;
        }

        var call = new IngressCall(
            request.HttpMethod ?? "",
            request.Url?.AbsolutePath ?? "/",
            TokenOf(request),
            body,
            request.RemoteEndPoint?.Address.ToString() ?? "");

        await WriteAsync(context, _answer(call));
    }

    /// <summary>
    /// Токен из заголовка. Понимаются оба вида: свой заголовок и привычный
    /// <c>Authorization: Bearer</c>. Второй нужен не ради красоты — половина готовых
    /// инструментов умеет только его.
    /// </summary>
    private static string TokenOf(HttpListenerRequest request)
    {
        var own = request.Headers[IngressApi.TokenHeader];
        if (!string.IsNullOrWhiteSpace(own)) return own.Trim();

        var auth = request.Headers["Authorization"] ?? "";

        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? auth[7..].Trim()
            : "";
    }

    /// <summary>
    /// Прочитать тело с потолком. Потолок спрашивается ПО ХОДУ чтения, а не по заголовку длины:
    /// заголовок пишет отправитель, и верить ему в вопросе «сколько памяти сейчас занять»
    /// нельзя.
    /// </summary>
    private static async Task<string> ReadBodyAsync(HttpListenerRequest request)
    {
        if (!request.HasEntityBody) return "";

        var limit  = IngressGate.MaxBodyBytes;
        var buffer = new byte[81920];
        var memory = new MemoryStream();

        while (true)
        {
            var read = await request.InputStream.ReadAsync(buffer);
            if (read == 0) break;

            memory.Write(buffer, 0, read);

            if (memory.Length > limit)
                throw new InvalidDataException(
                    $"Тело сообщения больше потолка в {limit / 1024 / 1024} МБ.");
        }

        return (request.ContentEncoding ?? Encoding.UTF8).GetString(memory.ToArray());
    }

    private static async Task WriteAsync(HttpListenerContext context, IngressAnswer answer)
    {
        var response = context.Response;
        var bytes    = Encoding.UTF8.GetBytes(answer.Body);

        response.StatusCode      = answer.Status;
        response.ContentType     = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;

        try
        {
            await response.OutputStream.WriteAsync(bytes);
        }
        catch (Exception failure) when (failure is HttpListenerException or ObjectDisposedException)
        {
            // Отправитель ушел, не дождавшись ответа. Для нас это норма: работа уже сделана, и
            // защита от повтора вернет ему тот же ответ, когда он постучится второй раз.
        }
        finally
        {
            response.Close();
        }
    }
}
