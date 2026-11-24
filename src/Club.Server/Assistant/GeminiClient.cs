using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Club.Server.Assistant;

/// <summary>Відповідь моделі: вміст (зберігається в історії як є) і виклики функцій.</summary>
public record ModelTurn(JsonObject Content, string Text, List<FunctionCall> Calls, string Model = "");

public record FunctionCall(string Name, JsonObject Args, string? Id);

public class AssistantException(string message) : Exception(message);

/// <summary>
/// Мовна модель для асистента. Історія розмови й описи функцій — у форматі Gemini;
/// інші постачальники (локальна модель через Ollama) перетворюють їх у свій формат.
/// </summary>
public interface IModelClient
{
    Task<ModelTurn> GenerateAsync(string systemPrompt, JsonArray contents, JsonArray functionDeclarations,
        CancellationToken ct, string? model = null);
}

/// <summary>
/// Виклик Gemini API (generateContent) з описами функцій панелі.
/// Ключ API зберігається лише на сервері; браузер його ніколи не бачить.
/// </summary>
public class GeminiClient(HttpClient http, AssistantSettings settings, ILogger<GeminiClient>? log = null) : IModelClient
{
    /// <summary>
    /// Запит до моделі. Якщо model не задано — пробує основну модель, а при перевантаженні, ліміті
    /// чи відсутності відповіді переходить на запасні (FallbackModels). Усі кроки одного запиту
    /// адміністратора виконує та сама модель (її назва повертається в ModelTurn.Model).
    /// </summary>
    public async Task<ModelTurn> GenerateAsync(string systemPrompt, JsonArray contents, JsonArray functionDeclarations,
        CancellationToken ct, string? model = null)
    {
        var key = settings.ApiKey ?? throw new AssistantException("Асистент не налаштовано: немає ключа API.");
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = systemPrompt }) },
            ["contents"] = contents.DeepClone(),
            ["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = functionDeclarations.DeepClone() }),
            ["toolConfig"] = new JsonObject { ["functionCallingConfig"] = new JsonObject { ["mode"] = "AUTO" } }
        };
        // Менша «глибина роздумів» — швидша відповідь; для вибору функції панелі цього досить
        if (!string.IsNullOrWhiteSpace(settings.ThinkingLevel))
            body["generationConfig"] = new JsonObject { ["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = settings.ThinkingLevel } };
        var requestJson = body.ToJsonString();

        var models = model != null ? new[] { model } : new[] { settings.Model }.Concat(settings.FallbackModels).Distinct().ToArray();
        AssistantException? last = null;
        foreach (var m in models)
        {
            try
            {
                return await SendAsync(m, key, requestJson, ct);
            }
            catch (RetryableException ex)
            {
                last = new AssistantException(ex.Message);
                if (m != models[^1]) log?.LogWarning("Модель {Model}: {Reason} — пробую запасну", m, ex.Message);
            }
        }
        throw last ?? new AssistantException("Немає доступних моделей.");
    }

    private class RetryableException(string message) : Exception(message);

    private async Task<ModelTurn> SendAsync(string model, string key, string requestJson, CancellationToken ct)
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-goog-api-key", key);

        // Обмеження однієї спроби: краще швидко перейти на запасну модель, ніж хвилину чекати
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(settings.AttemptTimeout);

        HttpResponseMessage response;
        string text;
        try
        {
            response = await http.SendAsync(request, attempt.Token);
            text = await response.Content.ReadAsStringAsync(attempt.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RetryableException($"Gemini ({model}) не відповів за {settings.AttemptTimeout.TotalSeconds:0} с. Спробуйте ще раз.");
        }
        catch (HttpRequestException ex)
        {
            // Точна причина (DNS, проксі, сертифікат, блокування) — у консоль сервера і в повідомлення
            var reason = ex.GetBaseException().Message;
            log?.LogWarning(ex, "Немає зв'язку з Gemini: {Reason}", reason);
            throw new AssistantException($"Немає зв'язку з сервісом Gemini: {reason}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            switch (response.StatusCode)
            {
                case HttpStatusCode.TooManyRequests:
                    throw new RetryableException("Вичерпано ліміт запитів до Gemini (безкоштовний рівень). Спробуйте за хвилину.");
                case HttpStatusCode.NotFound:
                    throw new RetryableException($"Модель «{model}» недоступна. Вкажіть іншу в appsettings.json (Assistant → Model).");
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    throw new AssistantException("Gemini відхилив ключ API. Перевірте ключ у файлі assistant-key.txt.");
                default:
                    if (status >= 500)
                        throw new RetryableException($"Сервіс Gemini перевантажений або недоступний ({status}). Спробуйте за хвилину.");
                    throw new AssistantException($"Помилка Gemini ({status}): {ErrorMessage(text)}");
            }
        }

        var json = JsonNode.Parse(text)!.AsObject();
        var candidate = json["candidates"]?[0];
        if (candidate?["content"] is not JsonObject content)
        {
            var reason = candidate?["finishReason"]?.GetValue<string>() ?? json["promptFeedback"]?["blockReason"]?.GetValue<string>();
            throw new AssistantException($"Модель не дала відповіді{(reason != null ? $" ({reason})" : "")}. Переформулюйте запит.");
        }
        content = (JsonObject)content.DeepClone();   // окремий вузол: він піде в історію розмови
        content["role"] ??= "model";

        var answer = new StringBuilder();
        var calls = new List<FunctionCall>();
        foreach (var part in content["parts"]?.AsArray() ?? new JsonArray())
        {
            if (part is null) continue;
            if (part["functionCall"] is JsonObject fc)
                calls.Add(new FunctionCall(
                    fc["name"]!.GetValue<string>(),
                    fc["args"] as JsonObject ?? new JsonObject(),
                    fc["id"]?.GetValue<string>()));
            else if (part["text"] is JsonValue t && part["thought"]?.GetValue<bool>() != true)
                answer.Append(t.GetValue<string>());
        }
        // Вміст моделі повертається в історію без змін — разом із підписами міркувань (thoughtSignature),
        // які Gemini вимагає при викликах функцій
        return new ModelTurn(content, answer.ToString().Trim(), calls, model);
    }

    /// <summary>
    /// Мережевий обробник для запитів до Gemini. Спершу пробує IPv4-адреси, кожну — не довше 5 с.
    /// Стандартний обробник .NET перебирає адреси по черзі з довгим тайм-аутом, і якщо IPv6 у мережі
    /// не працює (браузер це обходить), запит зависає на хвилину й довше.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(ILogger log) => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            foreach (var address in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                var sw = Stopwatch.StartNew();
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    await socket.ConnectAsync(address, context.DnsEndPoint.Port, timeout.Token);
                    log.LogDebug("З'єднано з {Host} ({Address}) за {Ms} мс", host, address, sw.ElapsedMilliseconds);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    log.LogWarning("Не вдалося з'єднатися з {Host} за адресою {Address} ({Ms} мс): {Message}",
                        host, address, sw.ElapsedMilliseconds, ex.Message);
                    socket.Dispose();
                }
            }
            throw new HttpRequestException($"жодна з адрес {host} ({string.Join(", ", addresses.Select(a => a.ToString()))}) не відповіла");
        }
    };

    private static string ErrorMessage(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? body; }
        catch { return body.Length > 200 ? body[..200] : body; }
    }
}
