using System.Security.Claims;
using Club.Server.Data;
using Club.Server.Security;

namespace Club.Server.Assistant;

public record ChatRequest(string? ConversationId, string? Message);
public record ProviderRequest(string? Provider);
public record ConfirmedRequest(string? ConversationId, string? ActionId, bool Accepted, bool Success, string? Result);

/// <summary>API AI-асистента панелі: /api/assistant/…  (лише для адміністраторів, що увійшли).</summary>
public static class AssistantApi
{
    public const string ProviderSettingKey = "Assistant.Provider";

    public static void MapAssistantApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/assistant").RequireAuthorization();

        api.MapGet("/status", async (AssistantSettings settings, OpenAiCompatibleClient openAi, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var (running, installed) = await openAi.CheckLocalAsync(ct);
            return Results.Ok(new
            {
                settings.Configured,
                settings.Provider,
                Model = settings.CurrentModel,
                CanSwitch = user.IsInRole(Roles.Owner),
                Providers = new object[]
                {
                    new { Id = AssistantSettings.Cloud, Label = "Хмарна модель", settings.Model, Ready = settings.CloudConfigured,
                          Note = settings.CloudConfigured ? "" : "немає ключа API" },
                    new { Id = AssistantSettings.Local, Label = "Локальна модель", Model = settings.LocalModel,
                          Ready = running && installed,
                          Note = !running ? "сервер моделі не запущено" : !installed ? $"модель {settings.LocalModel} не завантажена" : "" }
                }
            });
        });

        // Перемикання постачальника моделі (лише власник). Вибір зберігається в базі й діє після перезапуску.
        api.MapPost("/provider", async (ProviderRequest req, AssistantSettings settings, AppDb db, ClaimsPrincipal user) =>
        {
            if (AssistantSettings.NormalizeProvider(req.Provider) is not { } provider)
                return Results.BadRequest(new { error = "Невідомий режим моделі" });
            settings.Provider = provider;
            var setting = await db.Settings.FindAsync(ProviderSettingKey);
            if (setting == null) db.Settings.Add(new Setting { Key = ProviderSettingKey, Value = provider });
            else setting.Value = provider;
            db.Log(null, $"AI-асистент: обрано {(settings.IsLocal ? "локальну" : "хмарну")} модель ({settings.CurrentModel})",
                user.Identity!.Name);
            await db.SaveChangesAsync();
            return Results.Ok(new { settings.Provider, Model = settings.CurrentModel });
        }).RequireAuthorization("Owner");

        api.MapPost("/chat", async (ChatRequest req, AssistantService assistant, ConversationStore store,
            AssistantSettings settings, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (!settings.Configured)
                return Results.BadRequest(new { error = "Асистент не налаштовано: покладіть ключ API хмарної моделі у файл assistant-key.txt поруч із сервером і перезапустіть сервер, або оберіть локальну модель." });
            var message = (req.Message ?? "").Trim();
            if (message.Length == 0) return Results.BadRequest(new { error = "Порожнє повідомлення" });
            if (message.Length > 1000) return Results.BadRequest(new { error = "Занадто довге повідомлення (до 1000 символів)" });

            var login = user.Identity!.Name!;
            var conv = store.Get(req.ConversationId, login);
            if (!await conv.Lock.WaitAsync(0, ct))
                return Results.BadRequest(new { error = "Асистент ще відповідає на попереднє повідомлення" });
            try
            {
                conv.LastUsed = DateTime.UtcNow;
                var reply = await assistant.ChatAsync(conv, message, login, user.IsInRole(Roles.Owner), ct);
                return Results.Ok(reply);
            }
            catch (AssistantException ex)
            {
                return Results.BadRequest(new { error = ex.Message, conversationId = conv.Id });
            }
            finally
            {
                conv.Lock.Release();
            }
        });

        // Панель повідомляє, що адміністратор підтвердив (і виконав) або скасував запропоновану дію.
        // Сама дія виконується через звичайні API панелі — тут лише журнал і примітка для моделі.
        api.MapPost("/confirmed", async (ConfirmedRequest req, ConversationStore store, AppDb db, ClaimsPrincipal user) =>
        {
            var login = user.Identity!.Name!;
            if (req.ConversationId == null || req.ActionId == null || store.Find(req.ConversationId, login) is not { } conv
                || !conv.Pending.TryRemove(req.ActionId, out var action))
                return Results.NotFound(new { error = "Дію не знайдено" });

            if (!req.Accepted)
            {
                conv.Notes.Add($"[Адміністратор СКАСУВАВ запропоновану дію: {action.Title}]");
                return Results.Ok();
            }
            var result = (req.Result ?? "").Length > 300 ? req.Result![..300] : req.Result ?? "";
            conv.Notes.Add(req.Success
                ? $"[Адміністратор підтвердив, дію виконано: {action.Title}. {result}]"
                : $"[Адміністратор підтвердив, але дія не вдалася: {action.Title}. {result}]");
            db.Log(null, $"AI-асистент: «{action.Title}» — підтверджено адміністратором{(req.Success ? "" : $", помилка: {result}")}", login);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        api.MapPost("/reset", (ChatRequest req, ConversationStore store, ClaimsPrincipal user) =>
        {
            if (req.ConversationId != null) store.Remove(req.ConversationId, user.Identity!.Name!);
            return Results.Ok();
        });
    }
}
