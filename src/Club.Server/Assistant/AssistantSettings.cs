namespace Club.Server.Assistant;

/// <summary>
/// Налаштування AI-асистента. Два режими, між якими перемикається власник у панелі:
/// - Cloud (хмарна модель): Gemini API або будь-яке OpenAI-сумісне API (OpenAI, Groq, OpenRouter тощо) — потрібен ключ;
/// - Local (локальна модель): OpenAI-сумісний сервер на цьому комп'ютері (Ollama, LM Studio) — без інтернету.
/// </summary>
public class AssistantSettings
{
    public const string Cloud = "Cloud";
    public const string Local = "Local";
    public const string GeminiApi = "Gemini";
    public const string OpenAiApi = "OpenAI";

    /// <summary>Поточний режим: Cloud або Local. Перемикає власник у панелі, вибір зберігається в базі.</summary>
    public string Provider { get; set; } = Cloud;

    // ---------- Хмарна модель ----------
    /// <summary>Формат API хмарної моделі: Gemini або OpenAI (OpenAI-сумісні сервіси).</summary>
    public string CloudApi { get; init; } = GeminiApi;
    /// <summary>Адреса OpenAI-сумісного API без /v1 (для Gemini не потрібна).</summary>
    public string CloudUrl { get; init; } = "https://api.openai.com";
    public string Model { get; init; } = "gemini-3.6-flash";
    public string[] FallbackModels { get; init; } = [];
    public string? ApiKey { get; init; }
    public string? ThinkingLevel { get; init; } = "low";
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(20);

    // ---------- Локальна модель ----------
    public string LocalUrl { get; init; } = "http://localhost:11434";
    public string LocalModel { get; init; } = "qwen3:8b";

    public bool IsLocal => Provider == Local;
    public string CurrentModel => IsLocal ? LocalModel : Model;
    public bool CloudConfigured => !string.IsNullOrWhiteSpace(ApiKey);
    public bool Configured => IsLocal || CloudConfigured;

    /// <summary>Старі назви режимів (до перейменування) теж розуміємо.</summary>
    public static string? NormalizeProvider(string? value) => value switch
    {
        Cloud or "Gemini" => Cloud,
        Local or "Ollama" => Local,
        _ => null
    };

    public static AssistantSettings Load(IConfiguration config, string contentRoot)
    {
        var root = config.GetSection("Assistant");
        var cloud = root.GetSection("Cloud");
        var local = root.GetSection("Local");
        string? Get(IConfigurationSection s, string name) => string.IsNullOrWhiteSpace(s[name]) ? root[name] : s[name];

        // Ключ: appsettings → змінна середовища → файл assistant-key.txt поруч із сервером
        var key = Get(cloud, "ApiKey");
        if (string.IsNullOrWhiteSpace(key)) key = Environment.GetEnvironmentVariable("ASSISTANT_API_KEY")
                                              ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        var keyFile = Path.Combine(contentRoot, Get(cloud, "ApiKeyFile") ?? "assistant-key.txt");
        if (string.IsNullOrWhiteSpace(key) && File.Exists(keyFile)) key = File.ReadAllText(keyFile).Trim();

        var api = Get(cloud, "Api")?.Equals(OpenAiApi, StringComparison.OrdinalIgnoreCase) == true ? OpenAiApi : GeminiApi;
        var fallback = cloud.GetSection("FallbackModels").Get<string[]>() ?? root.GetSection("FallbackModels").Get<string[]>() ?? [];
        return new AssistantSettings
        {
            Provider = NormalizeProvider(root["Provider"]) ?? Cloud,
            CloudApi = api,
            CloudUrl = (Get(cloud, "Url") ?? "https://api.openai.com").TrimEnd('/'),
            Model = Get(cloud, "Model") ?? (api == GeminiApi ? "gemini-3.6-flash" : "gpt-4o-mini"),
            FallbackModels = fallback,
            ApiKey = string.IsNullOrWhiteSpace(key) ? null : key,
            ThinkingLevel = Get(cloud, "ThinkingLevel") ?? "low",
            AttemptTimeout = TimeSpan.FromSeconds(int.TryParse(Get(cloud, "AttemptTimeoutSeconds"), out var t) && t > 0 ? t : 20),
            LocalUrl = (local["Url"] ?? root["Ollama:Url"] ?? "http://localhost:11434").TrimEnd('/'),
            LocalModel = local["Model"] ?? root["Ollama:Model"] ?? "qwen3:8b"
        };
    }
}
