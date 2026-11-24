using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Club.Server.Assistant;
using Club.Server.Data;
using Club.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Club.Tests;

/// <summary>
/// Підробний сервер Gemini: повертає заготовлені відповіді по черзі й запам'ятовує запити,
/// тож тести перевіряють логіку асистента без інтернету і ключа API.
/// </summary>
public class FakeGemini : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();
    public List<JsonObject> Requests { get; } = [];
    public List<string> Urls { get; } = [];
    public List<string?> Authorization { get; } = [];

    public FakeGemini Call(string name, JsonObject args, string signature = "sig-1")
    {
        var part = new JsonObject { ["functionCall"] = new JsonObject { ["name"] = name, ["args"] = args }, ["thoughtSignature"] = signature };
        return Enqueue(new JsonObject { ["role"] = "model", ["parts"] = new JsonArray(part) });
    }

    public FakeGemini Text(string text) =>
        Enqueue(new JsonObject { ["role"] = "model", ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) });

    public FakeGemini Fail(HttpStatusCode status)
    {
        _responses.Enqueue((status, """{"error":{"message":"test"}}"""));
        return this;
    }

    /// <summary>Довільна відповідь (напр., у форматі OpenAI для локальної моделі).</summary>
    public FakeGemini Raw(JsonObject body)
    {
        _responses.Enqueue((HttpStatusCode.OK, body.ToJsonString()));
        return this;
    }

    private FakeGemini Enqueue(JsonObject content)
    {
        var body = new JsonObject { ["candidates"] = new JsonArray(new JsonObject { ["content"] = content }) };
        _responses.Enqueue((HttpStatusCode.OK, body.ToJsonString()));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
        Urls.Add(request.RequestUri!.ToString());
        Authorization.Add(request.Headers.Authorization?.ToString());
        var (status, body) = _responses.Dequeue();
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

public sealed class AssistantTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly AppDb _db;
    private readonly FakeGemini _gemini = new();
    private readonly ConnectionRegistry _registry = new();
    private readonly AssistantService _assistant;
    private readonly Conversation _conv = new("c1", "boss");
    private readonly int _pc1, _pc2;

    public AssistantTests()
    {
        _db = _database.Create();
        var p1 = new Pc { MachineName = "CLUBPC-1", DisplayName = "ПК-1", LastSeen = DateTime.UtcNow };
        var p2 = new Pc { MachineName = "CLUBPC-2", DisplayName = "ПК-2", LastSeen = DateTime.UtcNow };
        _db.Pcs.AddRange(p1, p2);
        _db.Clients.Add(new Client { Login = "ivan", PasswordHash = "x", BalanceMinutes = 30, CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();
        _pc1 = p1.Id; _pc2 = p2.Id;
        _registry.Add(_pc1, "conn-1");                       // ПК-1 у мережі, ПК-2 — ні

        var settings = new AssistantSettings { ApiKey = "test-key", Model = "test-model" };
        var client = new GeminiClient(new HttpClient(_gemini), settings);
        _assistant = new AssistantService(client, _db, _registry, NullLogger<AssistantService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _database.Dispose();
    }

    private Task<ChatReply> Ask(string text, bool owner = true) => _assistant.ChatAsync(_conv, text, "boss", owner, CancellationToken.None);

    private JsonObject LastFunctionResponse() =>
        _gemini.Requests[^1]["contents"]!.AsArray()[^1]!["parts"]![0]!["functionResponse"]!["response"]!.AsObject();

    [Fact]
    public async Task Journal_filter_is_returned_to_panel_with_resolved_pc()
    {
        _gemini.Call("filter_journal", new JsonObject { ["pc"] = "пк2", ["category"] = "client", ["period"] = "today" })
               .Text("Готово, показую журнал ПК-2 за сьогодні.");

        var reply = await Ask("покажи журнал другого ПК за сьогодні, тільки клієнтів");

        var action = Assert.Single(reply.Actions);
        Assert.Equal("filterEvents", action.Type);
        Assert.Equal(_pc2.ToString(), action.Params["pc"]!.GetValue<string>());
        Assert.Equal("client", action.Params["category"]!.GetValue<string>());
        Assert.Equal("today", action.Params["period"]!.GetValue<string>());
        Assert.Equal("Готово, показую журнал ПК-2 за сьогодні.", reply.Reply);
    }

    [Fact]
    public async Task Thought_signature_is_sent_back_to_gemini()
    {
        _gemini.Call("get_pcs_status", new JsonObject(), signature: "abc").Text("ПК-1 вільний.");

        await Ask("що з комп'ютерами?");

        var history = _gemini.Requests[1]["contents"]!.AsArray();
        Assert.Equal("abc", history[1]!["parts"]![0]!["thoughtSignature"]!.GetValue<string>());
        Assert.NotNull(history[2]!["parts"]![0]!["functionResponse"]);
    }

    [Fact]
    public async Task Pc_status_reports_online_and_offline()
    {
        _gemini.Call("get_pcs_status", new JsonObject()).Text("ok");
        await Ask("стан ПК");

        var pcs = LastFunctionResponse()["pcs"]!.AsArray();
        Assert.Equal("вільний", pcs.Single(p => p!["pc"]!.GetValue<string>() == "ПК-1")!["state"]!.GetValue<string>());
        Assert.Equal("поза мережею", pcs.Single(p => p!["pc"]!.GetValue<string>() == "ПК-2")!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Changing_action_is_only_proposed_and_waits_for_confirmation()
    {
        _gemini.Call("pc_command", new JsonObject { ["pcs"] = new JsonArray("1"), ["command"] = "lock" })
               .Text("Підтвердіть блокування ПК-1.");

        var reply = await Ask("заблокуй перший");

        var c = Assert.Single(reply.Confirmations);
        Assert.Equal("command", c.Kind);
        Assert.Equal("Lock", c.Params["type"]!.GetValue<string>());
        Assert.Equal(_pc1, c.Params["pcIds"]![0]!.GetValue<int>());
        Assert.True(_conv.Pending.ContainsKey(c.Id));
        Assert.Equal("awaiting_confirmation", LastFunctionResponse()["status"]!.GetValue<string>());
        Assert.Empty(reply.Actions);
    }

    [Fact]
    public async Task Offline_pcs_are_skipped_in_commands()
    {
        _gemini.Call("pc_command", new JsonObject { ["pcs"] = new JsonArray("ПК-1", "ПК-2"), ["command"] = "reboot" }).Text("ok");

        var reply = await Ask("перезавантаж обидва");

        Assert.Single(Assert.Single(reply.Confirmations).Params["pcIds"]!.AsArray());
        Assert.Contains("ПК-2", LastFunctionResponse()["note"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_pc_returns_error_to_model_with_known_names()
    {
        _gemini.Call("open_pc", new JsonObject { ["pc"] = "ПК-9" }).Text("Такого ПК немає.");

        var reply = await Ask("відкрий ПК-9");

        Assert.Empty(reply.Actions);
        var error = LastFunctionResponse()["error"]!.GetValue<string>();
        Assert.Contains("ПК-1", error);
        Assert.Contains("ПК-2", error);
    }

    [Fact]
    public async Task Operator_cannot_open_owner_tabs()
    {
        _gemini.Call("show_tab", new JsonObject { ["tab"] = "admins" }).Text("Немає доступу.");

        var reply = await Ask("відкрий адміністраторів", owner: false);

        Assert.Empty(reply.Actions);
        Assert.NotNull(LastFunctionResponse()["error"]);
    }

    [Fact]
    public async Task Owner_only_functions_are_not_offered_to_operator()
    {
        _gemini.Text("ok");
        await Ask("привіт", owner: false);

        var names = _gemini.Requests[0]["tools"]![0]!["functionDeclarations"]!.AsArray().Select(f => f!["name"]!.GetValue<string>());
        Assert.DoesNotContain("get_session_stats", names);
    }

    [Fact]
    public async Task Wrong_values_are_rejected_before_confirmation()
    {
        _gemini.Call("top_up_client", new JsonObject { ["login"] = "ivan", ["minutes"] = 0 }).Text("Некоректна кількість.");

        var reply = await Ask("поповни івана на нуль");

        Assert.Empty(reply.Confirmations);
        Assert.NotNull(LastFunctionResponse()["error"]);
    }

    [Fact]
    public async Task Gemini_error_keeps_history_unchanged()
    {
        _gemini.Text("перша відповідь");
        await Ask("перше питання");
        var before = _conv.Contents.Count;

        _gemini.Fail(HttpStatusCode.TooManyRequests);
        var ex = await Assert.ThrowsAsync<AssistantException>(() => Ask("друге питання"));

        Assert.Contains("ліміт", ex.Message);
        Assert.Equal(before, _conv.Contents.Count);
    }

    [Fact]
    public async Task Confirmation_notes_are_passed_to_model_with_next_message()
    {
        _conv.Notes.Add("[Адміністратор підтвердив, дію виконано: Заблокувати — ПК-1]");
        _gemini.Text("ok");

        await Ask("що далі?");

        var text = _gemini.Requests[0]["contents"]!.AsArray()[^1]!["parts"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("підтвердив", text);
        Assert.Contains("що далі?", text);
        Assert.Empty(_conv.Notes);
    }

    [Fact]
    public async Task Model_cannot_loop_forever()
    {
        for (var i = 0; i < 10; i++) _gemini.Call("get_pcs_status", new JsonObject());

        var reply = await Ask("цикл");

        Assert.Contains("кілька кроків", reply.Reply);
        Assert.Equal(6, _gemini.Requests.Count);
    }

    [Fact]
    public async Task Overloaded_model_falls_back_and_same_model_finishes_the_request()
    {
        var fake = new FakeGemini()
            .Fail(HttpStatusCode.ServiceUnavailable)                         // основна модель перевантажена
            .Call("get_pcs_status", new JsonObject())                        // запасна викликає функцію
            .Text("ПК-1 вільний.");                                          // і вона ж завершує відповідь
        var settings = new AssistantSettings { ApiKey = "k", Model = "main", FallbackModels = ["lite"] };
        var assistant = new AssistantService(new GeminiClient(new HttpClient(fake), settings), _db, _registry,
            NullLogger<AssistantService>.Instance);

        var reply = await assistant.ChatAsync(new Conversation("c2", "boss"), "що вільне?", "boss", true, CancellationToken.None);

        Assert.Equal("ПК-1 вільний.", reply.Reply);
        Assert.Equal("lite", reply.Model);
        Assert.Equal(3, fake.Requests.Count);
        Assert.Equal(new[] { "main", "lite", "lite" }, fake.Urls.Select(u => u.Split("/models/")[1].Split(':')[0]));
    }

    [Fact]
    public void Conversation_belongs_to_its_admin()
    {
        var store = new ConversationStore();
        var mine = store.Get(null, "boss");

        Assert.Same(mine, store.Get(mine.Id, "boss"));
        Assert.NotSame(mine, store.Get(mine.Id, "other"));   // інший адміністратор отримує нову розмову
        Assert.Null(store.Find(mine.Id, "other"));
    }
}

/// <summary>OpenAI-сумісні моделі (локальна через Ollama/LM Studio або хмарна): перетворення форматів і повний цикл.</summary>
public sealed class OpenAiAdapterTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly AppDb _db;

    public OpenAiAdapterTests()
    {
        _db = _database.Create();
        _db.Pcs.Add(new Pc { MachineName = "CLUBPC-1", DisplayName = "ПК-1", LastSeen = DateTime.UtcNow });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _database.Dispose();
    }

    private static JsonObject OpenAiReply(string? text, params (string Id, string Name, string Args)[] calls)
    {
        var message = new JsonObject { ["role"] = "assistant", ["content"] = text };
        if (calls.Length > 0)
            message["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id, ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Args }
            }).ToArray());
        return new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = message }) };
    }

    [Fact]
    public void History_is_converted_to_openai_messages()
    {
        var contents = new JsonArray(
            new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = "заблокуй ПК-1" }) },
            new JsonObject { ["role"] = "model", ["parts"] = new JsonArray(new JsonObject
            {
                ["functionCall"] = new JsonObject { ["name"] = "pc_command", ["args"] = new JsonObject { ["command"] = "lock" }, ["id"] = "c1" },
                ["thoughtSignature"] = "sig"
            }) },
            new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject
            {
                ["functionResponse"] = new JsonObject { ["name"] = "pc_command", ["id"] = "c1", ["response"] = new JsonObject { ["status"] = "ok" } }
            }) });

        var messages = OpenAiCompatibleClient.ToMessages("правила", contents);

        Assert.Equal(new[] { "system", "user", "assistant", "tool" }, messages.Select(m => m!["role"]!.GetValue<string>()));
        var call = messages[2]!["tool_calls"]![0]!;
        Assert.Equal("c1", call["id"]!.GetValue<string>());
        Assert.Equal("pc_command", call["function"]!["name"]!.GetValue<string>());
        Assert.Contains("lock", call["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal("c1", messages[3]!["tool_call_id"]!.GetValue<string>());
    }

    [Fact]
    public void Function_schemas_use_lowercase_types()
    {
        var tools = OpenAiCompatibleClient.ToTools(AssistantTools.Declarations(isOwner: true));

        var json = tools.ToJsonString();
        Assert.DoesNotContain("\"OBJECT\"", json);
        Assert.DoesNotContain("\"STRING\"", json);
        Assert.Contains("\"type\":\"function\"", json);
        Assert.Equal(AssistantTools.Declarations(true).Count, tools.Count);
    }

    [Fact]
    public void Response_with_tool_call_becomes_function_call_and_thinking_is_removed()
    {
        var turn = OpenAiCompatibleClient.FromResponse(OpenAiReply("<think>міркую…</think>Відкриваю.",
            ("call_7", "open_pc", "{\"pc\":\"ПК-1\"}")));

        Assert.Equal("Відкриваю.", turn.Text);
        var call = Assert.Single(turn.Calls);
        Assert.Equal("open_pc", call.Name);
        Assert.Equal("call_7", call.Id);
        Assert.Equal("ПК-1", call.Args["pc"]!.GetValue<string>());
    }

    [Fact]
    public async Task Assistant_works_end_to_end_with_local_model()
    {
        var fake = new FakeGemini()
            .Raw(OpenAiReply(null, ("call_1", "open_pc", "{\"pc\":\"1\"}")))
            .Raw(OpenAiReply("Відкрив ПК-1."));
        var settings = new AssistantSettings { Provider = AssistantSettings.Local, LocalModel = "qwen3:8b" };
        var assistant = new AssistantService(new OpenAiCompatibleClient(new HttpClient(fake), settings), _db, new ConnectionRegistry(),
            NullLogger<AssistantService>.Instance);

        var reply = await assistant.ChatAsync(new Conversation("c", "boss"), "відкрий перший ПК", "boss", true, CancellationToken.None);

        Assert.Equal("Відкрив ПК-1.", reply.Reply);
        Assert.Equal("qwen3:8b", reply.Model);
        Assert.Equal("openPc", Assert.Single(reply.Actions).Type);
        Assert.EndsWith("/v1/chat/completions", fake.Urls[0]);
        // Другий запит містить результат функції в ролі tool
        var secondMessages = fake.Requests[1]["messages"]!.AsArray();
        Assert.Equal("tool", secondMessages[^1]!["role"]!.GetValue<string>());
        Assert.Equal("call_1", secondMessages[^1]!["tool_call_id"]!.GetValue<string>());
    }

    [Fact]
    public void Local_model_needs_no_api_key()
    {
        var settings = new AssistantSettings { Provider = AssistantSettings.Local };
        Assert.True(settings.Configured);
        Assert.False(settings.CloudConfigured);
        settings.Provider = AssistantSettings.Cloud;
        Assert.False(settings.Configured);
    }

    [Theory]
    [InlineData("Cloud", "Cloud")]
    [InlineData("Local", "Local")]
    [InlineData("Gemini", "Cloud")]      // назви до перейменування, збережені в базі
    [InlineData("Ollama", "Local")]
    [InlineData("other", null)]
    public void Provider_names_are_normalized(string value, string? expected) =>
        Assert.Equal(expected, AssistantSettings.NormalizeProvider(value));

    [Fact]
    public async Task Cloud_openai_compatible_api_sends_key_and_model()
    {
        var fake = new FakeGemini().Raw(OpenAiReply("Готово."));
        var settings = new AssistantSettings
        {
            Provider = AssistantSettings.Cloud, CloudApi = AssistantSettings.OpenAiApi,
            CloudUrl = "https://api.example.com", Model = "some-model", ApiKey = "secret"
        };
        var client = new OpenAiCompatibleClient(new HttpClient(fake), settings);

        var turn = await client.GenerateAsync("p", new JsonArray(new JsonObject
        {
            ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = "привіт" })
        }), AssistantTools.Declarations(false), CancellationToken.None);

        Assert.Equal("Готово.", turn.Text);
        Assert.Equal("https://api.example.com/v1/chat/completions", fake.Urls[0]);
        Assert.Equal("Bearer secret", fake.Authorization[0]);
        Assert.Equal("some-model", fake.Requests[0]["model"]!.GetValue<string>());
    }
}

