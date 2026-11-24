using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Club.Server.Assistant;

/// <summary>
/// Адаптер для будь-якої моделі з OpenAI-сумісним API (/v1/chat/completions):
/// - локальної (Ollama, LM Studio) — без інтернету, дані не залишають комп'ютер;
/// - хмарної (OpenAI, Groq, OpenRouter тощо) — з ключем API.
/// Перетворює історію й описи функцій з формату Gemini у формат OpenAI і відповідь моделі — назад,
/// тому решта асистента не залежить від постачальника.
/// </summary>
public class OpenAiCompatibleClient(HttpClient http, AssistantSettings settings, ILogger<OpenAiCompatibleClient>? log = null) : IModelClient
{
    public async Task<ModelTurn> GenerateAsync(string systemPrompt, JsonArray contents, JsonArray functionDeclarations,
        CancellationToken ct, string? model = null)
    {
        var local = settings.IsLocal;
        var modelName = local ? settings.LocalModel : settings.Model;
        var baseUrl = local ? settings.LocalUrl : settings.CloudUrl;
        var where = local ? "Локальна модель" : "Хмарна модель";
        var body = new JsonObject
        {
            ["model"] = modelName,
            ["messages"] = ToMessages(systemPrompt, contents),
            ["tools"] = ToTools(functionDeclarations),
            ["stream"] = false,
            ["temperature"] = 0.2
        };

        HttpResponseMessage response;
        string text;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/chat/completions")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            if (!local)
            {
                if (settings.ApiKey == null) throw new AssistantException("Не задано ключ API хмарної моделі (assistant-key.txt).");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.ApiKey);
            }
            response = await http.SendAsync(request, ct);
            text = await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            log?.LogWarning("{Where} недоступна: {Message}", where, ex.GetBaseException().Message);
            throw new AssistantException(local
                ? $"Локальна модель недоступна за адресою {baseUrl}. Запустіть сервер моделі (напр., Ollama)."
                : $"Немає зв'язку з хмарною моделлю ({baseUrl}): {ex.GetBaseException().Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AssistantException(local
                ? "Локальна модель не відповіла вчасно (перший запуск моделі може тривати до хвилини). Спробуйте ще раз."
                : "Хмарна модель не відповіла вчасно. Спробуйте ще раз.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var message = ErrorMessage(text);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new AssistantException($"{where}: ключ API відхилено. Перевірте assistant-key.txt.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new AssistantException($"{where}: вичерпано ліміт запитів. Спробуйте за хвилину.");
            if (response.StatusCode == HttpStatusCode.NotFound || message.Contains("not found", StringComparison.OrdinalIgnoreCase))
                throw new AssistantException(local
                    ? $"Модель «{modelName}» не знайдена на локальному сервері (для Ollama: ollama pull {modelName})."
                    : $"Модель «{modelName}» недоступна. Вкажіть іншу в appsettings.json.");
            if (message.Contains("does not support tools", StringComparison.OrdinalIgnoreCase))
                throw new AssistantException($"Модель «{modelName}» не вміє викликати функції. Оберіть іншу в appsettings.json.");
            throw new AssistantException($"Помилка: {where.ToLowerInvariant()} ({(int)response.StatusCode}): {message}");
        }

        var turn = FromResponse(JsonNode.Parse(text)!.AsObject());
        return turn with { Model = modelName };
    }

    /// <summary>Чи запущено локальний сервер моделі і чи є на ньому потрібна модель (для панелі).</summary>
    public async Task<(bool Running, bool ModelInstalled)> CheckLocalAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            // Список моделей у форматі OpenAI підтримують і Ollama, і LM Studio
            var json = JsonNode.Parse(await http.GetStringAsync($"{settings.LocalUrl}/v1/models", timeout.Token));
            var names = json?["data"]?.AsArray().Select(m => m?["id"]?.GetValue<string>() ?? "") ?? Enumerable.Empty<string>();
            var wanted = settings.LocalModel;
            return (true, names.Any(n => n.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                                      || n.Equals(wanted + ":latest", StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return (false, false);
        }
    }

    // ---------------------------------------------------------------- Gemini → OpenAI

    /// <summary>Історія у форматі Gemini → повідомлення OpenAI (system / user / assistant з tool_calls / tool).</summary>
    public static JsonArray ToMessages(string systemPrompt, JsonArray contents)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = systemPrompt } };
        foreach (var content in contents)
        {
            var role = content?["role"]?.GetValue<string>();
            var parts = content?["parts"]?.AsArray() ?? new JsonArray();
            if (role == "model")
            {
                var text = string.Concat(parts.Where(p => p?["text"] != null && p["thought"]?.GetValue<bool>() != true)
                                              .Select(p => p!["text"]!.GetValue<string>()));
                var calls = new JsonArray();
                foreach (var fc in parts.Select(p => p?["functionCall"]).OfType<JsonObject>())
                    calls.Add(new JsonObject
                    {
                        ["id"] = fc["id"]?.GetValue<string>() ?? fc["name"]!.GetValue<string>(),
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = fc["name"]!.GetValue<string>(),
                            ["arguments"] = (fc["args"] ?? new JsonObject()).ToJsonString()
                        }
                    });
                var message = new JsonObject { ["role"] = "assistant", ["content"] = text };
                if (calls.Count > 0) message["tool_calls"] = calls;
                messages.Add(message);
            }
            else
            {
                foreach (var part in parts)
                {
                    if (part?["functionResponse"] is JsonObject fr)
                        messages.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = fr["id"]?.GetValue<string>() ?? fr["name"]!.GetValue<string>(),
                            ["name"] = fr["name"]!.GetValue<string>(),
                            ["content"] = (fr["response"] ?? new JsonObject()).ToJsonString()
                        });
                    else if (part?["text"] is JsonValue t)
                        messages.Add(new JsonObject { ["role"] = "user", ["content"] = t.GetValue<string>() });
                }
            }
        }
        return messages;
    }

    /// <summary>Описи функцій Gemini → інструменти OpenAI (типи OBJECT/STRING → object/string).</summary>
    public static JsonArray ToTools(JsonArray functionDeclarations) =>
        new(functionDeclarations.Select(f =>
        {
            var fn = new JsonObject
            {
                ["name"] = f!["name"]!.GetValue<string>(),
                ["description"] = f["description"]?.GetValue<string>() ?? ""
            };
            fn["parameters"] = f["parameters"] is JsonObject p ? LowerTypes(p.DeepClone())
                : new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
            return (JsonNode)new JsonObject { ["type"] = "function", ["function"] = fn };
        }).ToArray());

    private static JsonNode LowerTypes(JsonNode node)
    {
        if (node is JsonObject o)
            foreach (var (key, value) in o.ToList())
            {
                if (key == "type" && value is JsonValue v && v.TryGetValue<string>(out var s)) o[key] = s.ToLowerInvariant();
                else if (value != null) LowerTypes(value);
            }
        else if (node is JsonArray a)
            foreach (var item in a) if (item != null) LowerTypes(item);
        return node;
    }

    // ---------------------------------------------------------------- OpenAI → Gemini

    /// <summary>Відповідь OpenAI (choices[0].message) → вміст у форматі Gemini з functionCall.</summary>
    public static ModelTurn FromResponse(JsonObject response)
    {
        var message = response["choices"]?[0]?["message"] ?? throw new AssistantException("Локальна модель не дала відповіді.");
        var text = message["content"] is JsonValue c && c.TryGetValue<string>(out var s) ? s : "";
        // Моделі з «міркуваннями» (qwen3 тощо) можуть повертати їх у тегах <think>
        text = Regex.Replace(text, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();

        var parts = new JsonArray();
        if (text.Length > 0) parts.Add(new JsonObject { ["text"] = text });
        var calls = new List<FunctionCall>();
        var index = 0;
        foreach (var call in message["tool_calls"]?.AsArray() ?? new JsonArray())
        {
            var name = call?["function"]?["name"]?.GetValue<string>();
            if (name == null) continue;
            var id = call!["id"]?.GetValue<string>() ?? $"call_{index}";
            index++;
            var args = ParseArguments(call["function"]!["arguments"]);
            parts.Add(new JsonObject { ["functionCall"] = new JsonObject { ["name"] = name, ["args"] = args.DeepClone(), ["id"] = id } });
            calls.Add(new FunctionCall(name, args, id));
        }
        var content = new JsonObject { ["role"] = "model", ["parts"] = parts };
        return new ModelTurn(content, text, calls);
    }

    private static JsonObject ParseArguments(JsonNode? arguments)
    {
        try
        {
            return arguments switch
            {
                JsonObject o => (JsonObject)o.DeepClone(),
                JsonValue v when v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) => JsonNode.Parse(s) as JsonObject ?? new JsonObject(),
                _ => new JsonObject()
            };
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static string ErrorMessage(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? JsonNode.Parse(body)?["error"]?.GetValue<string>() ?? body; }
        catch { return body.Length > 200 ? body[..200] : body; }
    }
}

/// <summary>
/// Обирає клієнт моделі за поточним режимом: локальна модель і хмарні OpenAI-сумісні API — через
/// OpenAiCompatibleClient, хмарний Gemini — через GeminiClient.
/// </summary>
public class ModelRouter(GeminiClient gemini, OpenAiCompatibleClient openAi, AssistantSettings settings) : IModelClient
{
    public Task<ModelTurn> GenerateAsync(string systemPrompt, JsonArray contents, JsonArray functionDeclarations,
        CancellationToken ct, string? model = null) =>
        settings.IsLocal || settings.CloudApi == AssistantSettings.OpenAiApi
            ? openAi.GenerateAsync(systemPrompt, contents, functionDeclarations, ct, model)
            : gemini.GenerateAsync(systemPrompt, contents, functionDeclarations, ct, model);
}
