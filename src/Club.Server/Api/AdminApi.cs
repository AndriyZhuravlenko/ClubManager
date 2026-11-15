using System.Security.Claims;
using Club.Server.Data;
using Club.Server.Security;
using Club.Server.Services;
using Club.Shared;
using Microsoft.EntityFrameworkCore;

namespace Club.Server.Api;

// Тіла запитів від панелі адміністратора
public record MinutesRequest(int Minutes);
public record CommandRequest(CommandType Type, string? Text);
public record RenameRequest(string Name);
public record NewClientRequest(string? Login, string? Password, int Minutes);
public record PasswordRequest(string? Password);
public record BlockRequest(bool Blocked);
public record NewAppRequest(string? Name, string? Path);
public record EnforceRequest(bool Enforce);
public record BlockRequestPattern(string? Pattern);

/// <summary>REST API для панелі адміністратора. Усі методи вимагають входу.</summary>
public static class AdminApi
{
    public static void MapAdminApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        // ---------- Комп'ютери ----------

        api.MapGet("/pcs", async (AppDb db, ConnectionRegistry registry) =>
        {
            var now = DateTime.UtcNow;
            var pcs = await db.Pcs.OrderBy(p => p.DisplayName).ToListAsync();
            var active = await db.Sessions.Where(s => s.ClosedAt == null).ToListAsync();
            var clientNames = await db.Clients.ToDictionaryAsync(c => c.Id, c => c.Login);

            return pcs.Select(p =>
            {
                var s = active.FirstOrDefault(x => x.PcId == p.Id);
                return new
                {
                    p.Id,
                    p.DisplayName,
                    p.MachineName,
                    Online = registry.IsOnline(p.Id),
                    p.LastSeen,
                    p.AgentVersion,
                    Agent = registry.GetMetrics(p.Id),
                    Session = s == null ? null : new
                    {
                        s.StartedAt,
                        s.EndsAt,
                        Client = s.ClientId != null && clientNames.TryGetValue(s.ClientId.Value, out var c) ? c : null,
                        s.ServiceAdmin,
                        Paused = s.LockedAt != null,
                        // Поки сеанс призупинено, відлік стоїть
                        RemainingSeconds = (int)Math.Max(0, (s.EndsAt - (s.LockedAt ?? now)).TotalSeconds)
                    }
                };
            });
        });

        api.MapPost("/pcs/{id:int}/session", async (int id, MinutesRequest req, SessionService sessions, ClaimsPrincipal user) =>
            ToResult(await sessions.StartAsync(id, req.Minutes, user.Identity!.Name)));

        api.MapPost("/pcs/{id:int}/session/extend", async (int id, MinutesRequest req, SessionService sessions, ClaimsPrincipal user) =>
            ToResult(await sessions.ExtendAsync(id, req.Minutes, user.Identity!.Name)));

        api.MapPost("/pcs/{id:int}/session/end", async (int id, SessionService sessions, ClaimsPrincipal user) =>
            ToResult(await sessions.EndAsync(id, "Завершено адміністратором", user.Identity!.Name)));

        api.MapPost("/pcs/{id:int}/command", async (int id, CommandRequest req, AgentCommander commander, ConnectionRegistry registry,
            AppDb db, SessionService sessions, ClaimsPrincipal user) =>
        {
            if (req.Type == CommandType.SessionUpdate) return Results.BadRequest(new { error = "Недопустима команда" });
            if (req.Type == CommandType.ShowMessage && string.IsNullOrWhiteSpace(req.Text))
                return Results.BadRequest(new { error = "Порожнє повідомлення" });

            // «Блок» під час сеансу — призупинення, «Розблок» призупиненого сеансу — відновлення
            var active = await sessions.GetActiveAsync(id);
            if (active != null && req.Type is CommandType.Lock or CommandType.Unlock)
            {
                if (!registry.IsOnline(id)) return Results.BadRequest(new { error = "ПК не в мережі" });
                if (req.Type == CommandType.Lock) return ToResult(await sessions.PauseAsync(id, user.Identity!.Name));
                if (active.LockedAt != null) return ToResult(await sessions.ResumeFromPanelAsync(id, user.Identity!.Name));
            }

            var sent = await commander.SendAsync(id, new AgentCommand(req.Type, req.Text));
            if (!sent) return Results.BadRequest(new { error = "ПК не в мережі" });

            db.Log(id, $"Команда: {req.Type}" + (req.Text != null ? $" «{req.Text}»" : ""), user.Identity!.Name);
            await db.SaveChangesAsync();

            // Вимкнений або перезавантажений ПК не може продовжувати сеанс
            if (req.Type is CommandType.Shutdown or CommandType.Reboot)
                await sessions.EndAsync(id, req.Type == CommandType.Shutdown
                    ? "ПК вимкнено адміністратором" : "ПК перезавантажено адміністратором", user.Identity!.Name);
            return Results.Ok();
        });

        api.MapPut("/pcs/{id:int}/name", async (int id, RenameRequest req, AppDb db, ClaimsPrincipal user) =>
        {
            var pc = await db.Pcs.FindAsync(id);
            if (pc == null) return Results.NotFound(new { error = "ПК не знайдено" });
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Порожня назва" });
            db.Log(id, $"ПК перейменовано: «{pc.DisplayName}» → «{req.Name.Trim()}»", user.Identity!.Name);
            pc.DisplayName = req.Name.Trim();
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // Прибрати ПК з панелі (списаний комп'ютер або тестові ПК після навантажувального тесту).
        // Лише для власника і лише для ПК поза мережею. Історія сеансів лишається у звітах.
        api.MapDelete("/pcs/{id:int}", async (int id, AppDb db, ConnectionRegistry registry, ClaimsPrincipal user) =>
        {
            var pc = await db.Pcs.FindAsync(id);
            if (pc == null) return Results.NotFound(new { error = "ПК не знайдено" });
            if (registry.IsOnline(id)) return Results.BadRequest(new { error = "ПК у мережі — спершу вимкніть його або видаліть агента" });
            if (await db.Sessions.AnyAsync(s => s.PcId == id && s.ClosedAt == null))
                return Results.BadRequest(new { error = "На ПК є незавершений сеанс" });

            db.Events.RemoveRange(db.Events.Where(e => e.PcId == id));
            db.Pcs.Remove(pc);
            db.Log(null, $"ПК «{pc.DisplayName}» ({pc.MachineName}) видалено з панелі", user.Identity!.Name);
            await db.SaveChangesAsync();
            return Results.Ok();
        }).RequireAuthorization("Owner");

        // ---------- Клієнти ----------

        api.MapGet("/clients", async (AppDb db) =>
        {
            var playing = await db.Sessions.Where(s => s.ClosedAt == null && s.ClientId != null)
                .Select(s => s.ClientId!.Value).ToListAsync();
            var clients = await db.Clients.OrderBy(c => c.Login).ToListAsync();
            return clients.Select(c => new { c.Id, c.Login, c.BalanceMinutes, c.IsBlocked, Playing = playing.Contains(c.Id) });
        });

        api.MapPost("/clients", async (NewClientRequest req, AppDb db, ClaimsPrincipal user) =>
        {
            var login = Passwords.NormalizeLogin(req.Login);
            if (login.Length < 3) return Results.BadRequest(new { error = "Логін має містити щонайменше 3 символи" });
            if (Passwords.Validate(req.Password) is { } bad) return Results.BadRequest(new { error = bad });
            if (req.Minutes is < 0 or > 10000) return Results.BadRequest(new { error = "Початковий баланс: від 0 до 10000 хв" });
            // Логін має бути унікальним серед і клієнтів, і адміністраторів: на екрані входу вони спільні
            if (await db.Clients.AnyAsync(c => c.Login == login) || await db.Admins.AnyAsync(a => a.Login == login))
                return Results.BadRequest(new { error = "Такий логін уже зайнятий" });

            var client = new Client { Login = login, PasswordHash = Passwords.Hash(req.Password!), BalanceMinutes = req.Minutes, CreatedAt = DateTime.UtcNow };
            db.Clients.Add(client);
            db.Log(null, $"Створено клієнта «{login}», баланс {req.Minutes} хв", user.Identity!.Name);
            await db.SaveChangesAsync();
            if (req.Minutes > 0)
            {
                // Початковий баланс теж рахується як поповнення (для звітів)
                db.TopUps.Add(new TopUp { ClientId = client.Id, Minutes = req.Minutes, At = DateTime.UtcNow, Actor = user.Identity!.Name });
                await db.SaveChangesAsync();
            }
            return Results.Ok();
        });

        api.MapPost("/clients/{id:int}/topup", async (int id, MinutesRequest req, SessionService sessions, ClaimsPrincipal user) =>
            ToResult(await sessions.TopUpAsync(id, req.Minutes, user.Identity!.Name)));

        // Видалення клієнта — лише власник (разом з обліковим записом зникає невикористаний баланс)
        api.MapDelete("/clients/{id:int}", async (int id, AppDb db, SessionService sessions, ClaimsPrincipal user) =>
        {
            var client = await db.Clients.FindAsync(id);
            if (client == null) return Results.NotFound(new { error = "Клієнта не знайдено" });

            // Якщо клієнт зараз грає — спершу завершуємо його сеанс
            var session = await db.Sessions.FirstOrDefaultAsync(s => s.ClientId == id && s.ClosedAt == null);
            if (session != null) await sessions.EndAsync(session.PcId, "Обліковий запис клієнта видалено", user.Identity!.Name);

            // Історія сеансів і поповнень залишається для звітів
            db.Clients.Remove(client);
            db.Log(null, $"Видалено клієнта «{client.Login}» (залишок балансу {client.BalanceMinutes} хв)", user.Identity!.Name);
            await db.SaveChangesAsync();
            return Results.Ok();
        }).RequireAuthorization("Owner");

        api.MapPost("/clients/{id:int}/password", async (int id, PasswordRequest req, AppDb db, ClaimsPrincipal user) =>
        {
            var client = await db.Clients.FindAsync(id);
            if (client == null) return Results.NotFound(new { error = "Клієнта не знайдено" });
            if (Passwords.Validate(req.Password) is { } bad) return Results.BadRequest(new { error = bad });

            client.PasswordHash = Passwords.Hash(req.Password!);
            db.Log(null, $"Змінено пароль клієнта «{client.Login}»", user.Identity!.Name);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        api.MapPost("/clients/{id:int}/block", async (int id, BlockRequest req, AppDb db, SessionService sessions, ClaimsPrincipal user) =>
        {
            var client = await db.Clients.FindAsync(id);
            if (client == null) return Results.NotFound(new { error = "Клієнта не знайдено" });

            client.IsBlocked = req.Blocked;
            db.Log(null, $"Клієнта «{client.Login}» {(req.Blocked ? "заблоковано" : "розблоковано")}", user.Identity!.Name);
            await db.SaveChangesAsync();

            // Заблокований клієнт одразу втрачає доступ
            if (req.Blocked)
            {
                var session = await db.Sessions.FirstOrDefaultAsync(s => s.ClientId == id && s.ClosedAt == null);
                if (session != null) await sessions.EndAsync(session.PcId, "Обліковий запис заблоковано", user.Identity!.Name);
            }
            return Results.Ok();
        });

        // ---------- Каталог дозволених програм ----------

        api.MapGet("/apps", async (AppPolicyService policy) => await policy.GetAsync());

        api.MapPost("/apps", async (NewAppRequest req, AppDb db, AppPolicyService policy, ClaimsPrincipal user) =>
        {
            var name = req.Name?.Trim() ?? "";
            var path = req.Path?.Trim().Trim('"') ?? "";
            if (name.Length == 0) return Results.BadRequest(new { error = "Вкажіть назву програми" });
            if (!System.IO.Path.IsPathFullyQualified(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = @"Вкажіть повний шлях до .exe, напр. C:\Games\Game\game.exe" });
            if (await db.Apps.AnyAsync(a => a.Path == path)) return Results.BadRequest(new { error = "Ця програма вже є в каталозі" });

            db.Apps.Add(new AppEntry { Name = name, Path = path });
            db.Log(null, $"Додано програму «{name}» ({path})", user.Identity!.Name);
            await db.SaveChangesAsync();
            await policy.BroadcastAsync();
            return Results.Ok();
        });

        api.MapDelete("/apps/{id:int}", async (int id, AppDb db, AppPolicyService policy, ClaimsPrincipal user) =>
        {
            var entry = await db.Apps.FindAsync(id);
            if (entry == null) return Results.NotFound(new { error = "Не знайдено" });
            db.Apps.Remove(entry);
            db.Log(null, $"Видалено програму «{entry.Name}»", user.Identity!.Name);
            await db.SaveChangesAsync();
            await policy.BroadcastAsync();
            return Results.Ok();
        });

        // Режим контролю змінює тільки власник
        api.MapPost("/apps/mode", async (EnforceRequest req, AppDb db, AppPolicyService policy, ClaimsPrincipal user) =>
        {
            await policy.SetEnforceAsync(req.Enforce);
            db.Log(null, req.Enforce
                ? "Режим контролю програм: ЗАКРИВАТИ недозволені"
                : "Режим контролю програм: лише СПОСТЕРЕЖЕННЯ", user.Identity!.Name);
            await db.SaveChangesAsync();
            await policy.BroadcastAsync();
            return Results.Ok();
        }).RequireAuthorization("Owner");

        // ---------- Чорний список програм ----------

        api.MapGet("/blocked", async (AppDb db) =>
            await db.BlockedApps.OrderBy(b => b.Pattern).Select(b => new { b.Id, b.Pattern }).ToListAsync());

        api.MapPost("/blocked", async (BlockRequestPattern req, AppDb db, AppPolicyService policy, ClaimsPrincipal user) =>
        {
            var pattern = req.Pattern?.Trim().Trim('"') ?? "";
            if (pattern.Length is 0 or > 260) return Results.BadRequest(new { error = "Вкажіть ім'я файлу або шлях" });
            if (pattern.Contains('\\'))
            {
                if (!System.IO.Path.IsPathFullyQualified(pattern))
                    return Results.BadRequest(new { error = @"Шлях має бути повним, напр. C:\Games\Old\ або C:\Tools\app.exe" });
            }
            else if (!pattern.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "Ім'я файлу має закінчуватися на .exe, напр. taskmgr.exe або cheat*.exe" });
            }
            if (await db.BlockedApps.AnyAsync(b => b.Pattern == pattern))
                return Results.BadRequest(new { error = "Вже є в чорному списку" });

            db.BlockedApps.Add(new BlockedApp { Pattern = pattern });
            db.Log(null, $"Додано до чорного списку: {pattern}", user.Identity!.Name);
            await db.SaveChangesAsync();
            await policy.BroadcastAsync();
            return Results.Ok();
        });

        api.MapDelete("/blocked/{id:int}", async (int id, AppDb db, AppPolicyService policy, ClaimsPrincipal user) =>
        {
            var entry = await db.BlockedApps.FindAsync(id);
            if (entry == null) return Results.NotFound(new { error = "Не знайдено" });
            db.BlockedApps.Remove(entry);
            db.Log(null, $"Прибрано з чорного списку: {entry.Pattern}", user.Identity!.Name);
            await db.SaveChangesAsync();
            await policy.BroadcastAsync();
            return Results.Ok();
        });

        // ---------- Журнал ----------

        // Фільтри: pc — "none" (події без ПК: панель, облікові записи) або Id ПК;
        // category — admin | client | agent | system; q — пошук у тексті; take — скільки записів
        api.MapGet("/events", async (string? pc, string? category, string? q, int? take, AppDb db) =>
        {
            var names = await db.Pcs.ToDictionaryAsync(p => p.Id, p => p.DisplayName);
            var adminLogins = await db.Admins.Select(a => a.Login).ToListAsync();
            var clientLogins = await db.Clients.Select(c => c.Login).ToListAsync();

            IQueryable<EventLog> query = db.Events;
            if (pc == "none") query = query.Where(e => e.PcId == null);
            else if (int.TryParse(pc, out var pcId)) query = query.Where(e => e.PcId == pcId);

            query = category switch
            {
                "admin" => query.Where(e => e.Actor != null && adminLogins.Contains(e.Actor)),
                "client" => query.Where(e => e.Actor != null && clientLogins.Contains(e.Actor)),
                "agent" => query.Where(e => e.Actor == "агент"),
                "system" => query.Where(e => e.Actor == null),
                _ => query
            };

            var limit = Math.Clamp(take ?? 100, 10, 1000);
            List<EventLog> events;
            if (string.IsNullOrWhiteSpace(q))
            {
                events = await query.OrderByDescending(e => e.Id).Take(limit).ToListAsync();
            }
            else
            {
                // SQLite не порівнює кирилицю без урахування регістру, тому шукаємо в пам'яті
                // серед останніх 5000 подій, що пройшли інші фільтри
                var text = q.Trim();
                events = (await query.OrderByDescending(e => e.Id).Take(5000).ToListAsync())
                    .Where(e => e.Text.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                             || (e.Actor ?? "").Contains(text, StringComparison.CurrentCultureIgnoreCase))
                    .Take(limit).ToList();
            }

            string CategoryOf(string? actor) =>
                actor == null ? "system"
                : actor == "агент" ? "agent"
                : adminLogins.Contains(actor) ? "admin"
                : clientLogins.Contains(actor) ? "client"
                : "other";

            return events.Select(e => new
            {
                e.At,
                e.PcId,
                Pc = e.PcId != null && names.TryGetValue(e.PcId.Value, out var n) ? n : "—",
                Actor = e.Actor ?? "система",
                Category = CategoryOf(e.Actor),
                e.Text
            });
        });
    }

    private static IResult ToResult(string? error) =>
        error == null ? Results.Ok() : Results.BadRequest(new { error });
}
