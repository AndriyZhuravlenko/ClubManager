using Club.Server.Data;
using Club.Server.Services;
using Club.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Club.Server.Hubs;

/// <summary>
/// "Точка зустрічі" агентів із сервером. Кожен ПК тримає з нею постійне
/// WebSocket-з'єднання: через нього сервер миттєво надсилає команди,
/// а агент — свій стан.
/// </summary>
public class AgentHub(AppDb db, ConnectionRegistry registry, SessionService sessions, AppPolicyService policy,
    OwnerCredentialsService owners, AgentCommander commander,
    IConfiguration config, ILogger<AgentHub> log) : Hub
{
    /// <summary>Група SignalR, до якої додаються лише агенти, що пройшли перевірку ключа.</summary>
    public const string AgentsGroup = "agents";

    public override Task OnConnectedAsync()
    {
        // Перевірка ключа агента: без правильного ключа з'єднання розривається.
        var key = Context.GetHttpContext()?.Request.Headers["X-Agent-Key"].ToString();
        if (string.IsNullOrEmpty(key) || key != config["AgentKey"])
        {
            log.LogWarning("Відхилено підключення з неправильним ключем ({Ip})",
                Context.GetHttpContext()?.Connection.RemoteIpAddress);
            Context.Abort();
        }
        return base.OnConnectedAsync();
    }

    /// <summary>Агент викликає одразу після підключення.</summary>
    public async Task<int> Register(AgentHello hello)
    {
        var pc = await db.Pcs.FirstOrDefaultAsync(p => p.MachineName == hello.MachineName);
        if (pc == null)
        {
            pc = new Pc { MachineName = hello.MachineName, DisplayName = hello.MachineName };
            db.Pcs.Add(pc);
        }
        pc.LastSeen = DateTime.UtcNow;
        await db.SaveChangesAsync();

        registry.Add(pc.Id, Context.ConnectionId);
        Context.Items["pcId"] = pc.Id;
        db.Log(pc.Id, $"ПК підключився (агент {hello.AgentVersion})");
        await db.SaveChangesAsync();

        // Спершу — політика доступу до програм, потім — стан робочого місця
        await Clients.Caller.SendAsync(HubMethods.AppPolicy, await policy.GetAsync());

        // Зареєстровані агенти отримують розсилки (політика програм, дані власників для офлайн-входу)
        await Groups.AddToGroupAsync(Context.ConnectionId, AgentsGroup);
        await Clients.Caller.SendAsync(HubMethods.OwnerCredentials, await owners.GetAsync());

        // Повідомляємо агенту, в якому стані він має бути зараз
        var active = await db.Sessions.FirstOrDefaultAsync(s => s.PcId == pc.Id && s.ClosedAt == null);
        var command = active == null
            ? new AgentCommand(CommandType.Lock)
            : active.LockedAt != null
                ? new AgentCommand(CommandType.Lock, Text: "Сеанс призупинено адміністратором.")
                : new AgentCommand(CommandType.Unlock, SessionEndsAt: active.EndsAt, Kind: SessionService.KindOf(active));
        await Clients.Caller.SendAsync(HubMethods.Command, command);

        return pc.Id;
    }

    /// <summary>Агент викликає кожні 15 секунд: "я живий" + власне навантаження на ПК.</summary>
    public async Task Heartbeat(double cpuPercent, double memoryMb)
    {
        // Id ПК збережено в даних з'єднання під час Register
        if (!Context.Items.TryGetValue("pcId", out var value) || value is not int pcId) return;
        registry.SetMetrics(pcId, new AgentMetrics(cpuPercent, memoryMb, DateTime.UtcNow));
        var pc = await db.Pcs.FindAsync(pcId);
        if (pc == null) return;
        pc.LastSeen = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>Клієнт вводить логін і пароль на екрані блокування (через агента).</summary>
    public async Task<LoginResult> ClientLogin(string? login, string? password)
    {
        if (!Context.Items.TryGetValue("pcId", out var value) || value is not int pcId)
            return new LoginResult(false, "ПК не зареєстровано");
        return await sessions.ClientLoginAsync(pcId, login, password);
    }

    /// <summary>Клієнт сам завершує свій сеанс.</summary>
    /// <param name="reason">null — користувач натиснув «Завершити»; "idle" — тривала неактивність.</param>
    public async Task<LoginResult> ClientLogout(string? reason)
    {
        if (!Context.Items.TryGetValue("pcId", out var value) || value is not int pcId)
            return new LoginResult(false, "ПК не зареєстровано");
        var error = await sessions.EndAsync(pcId, reason == "idle"
            ? "Неактивність понад 30 хв — сеанс завершено автоматично"
            : "Завершено користувачем");
        if (error != null)
        {
            // Сеансу на сервері немає (напр., ПК розблокували вручну) — просто блокуємо ПК
            await Clients.Caller.SendAsync(HubMethods.Command, new AgentCommand(CommandType.Lock));
            db.Log(pcId, "Користувач завершив роботу на ПК без сеансу");
            await db.SaveChangesAsync();
        }
        return new LoginResult(true);
    }

    /// <summary>Агент повідомляє про подію на ПК (напр., запуск недозволеної програми).</summary>
    public async Task Report(string text)
    {
        if (!Context.Items.TryGetValue("pcId", out var value) || value is not int pcId) return;
        if (string.IsNullOrWhiteSpace(text)) return;
        db.Log(pcId, text.Length > 500 ? text[..500] : text, "агент");
        await db.SaveChangesAsync();
    }

    /// <summary>Агент повідомляє, чи вдалося виконати команду.</summary>
    public async Task CommandResult(CommandType type, bool success, string? error)
    {
        if (!Context.Items.TryGetValue("pcId", out var value) || value is not int pcId) return;
        // Час від відправлення команди сервером до підтвердження від агента
        var ms = commander.TakeRoundTripMs(pcId, type);
        var timing = ms != null ? $" (за {ms.Value:0} мс)" : "";
        db.Log(pcId, success ? $"ПК виконав команду {type}{timing}" : $"ПК не зміг виконати {type}: {error}");
        await db.SaveChangesAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var pcId = registry.RemoveByConnection(Context.ConnectionId);
        if (pcId != null)
        {
            // Момент зникнення ПК — до нього списується час, якщо ПК не повернеться
            if (await db.Pcs.FindAsync(pcId.Value) is { } pc) pc.LastSeen = DateTime.UtcNow;
            db.Log(pcId, "ПК відключився");
            await db.SaveChangesAsync();
        }
        await base.OnDisconnectedAsync(exception);
    }
}
