using Club.Server.Data;
using Club.Server.Security;
using Club.Shared;
using Microsoft.EntityFrameworkCore;

namespace Club.Server.Services;

/// <summary>
/// Логіка сеансів. Сервер — єдине джерело правди про час:
/// клієнт лише показує таймер, а рішення про блокування приймає сервер.
///
/// Два види сеансів:
///  - гостьовий: відкриває адміністратор на N хвилин (ClientId = null);
///  - клієнтський: клієнт входить на екрані блокування, тривалість обмежена
///    його балансом, після завершення використані хвилини списуються;
///  - службовий: адміністратор входить на ПК без обмежень.
/// Сеанс можна призупинити (адміністратор блокує ПК) — час призупинення не оплачується.
///
/// Методи повертають текст помилки або null, якщо все добре.
/// </summary>
public class SessionService(AppDb db, AgentCommander commander, LoginThrottle throttle)
{
    public const int WarningMinutes = 5;

    public static SessionKind KindOf(Session s) =>
        s.ServiceAdmin != null ? SessionKind.Service : s.ClientId != null ? SessionKind.Client : SessionKind.Guest;

    public Task<Session?> GetActiveAsync(int pcId) =>
        db.Sessions.FirstOrDefaultAsync(s => s.PcId == pcId && s.ClosedAt == null);

    /// <summary>Гостьовий сеанс, який відкриває адміністратор.</summary>
    public async Task<string?> StartAsync(int pcId, int minutes, string? actor)
    {
        if (minutes is < 1 or > 1440) return "Тривалість має бути від 1 до 1440 хвилин";
        if (!await db.Pcs.AnyAsync(p => p.Id == pcId)) return "ПК не знайдено";
        if (await GetActiveAsync(pcId) != null) return "На цьому ПК уже є активний сеанс";

        var now = DateTime.UtcNow;
        var session = new Session { PcId = pcId, StartedAt = now, EndsAt = now.AddMinutes(minutes) };
        db.Sessions.Add(session);
        db.Log(pcId, $"Гостьовий сеанс розпочато на {minutes} хв", actor);
        await db.SaveChangesAsync();

        await commander.SendAsync(pcId, new AgentCommand(CommandType.Unlock, SessionEndsAt: session.EndsAt, Kind: SessionKind.Guest));
        return null;
    }

    /// <summary>Клієнт входить на екрані блокування свого ПК.</summary>
    public async Task<LoginResult> ClientLoginAsync(int pcId, string? login, string? password)
    {
        var normalized = Passwords.NormalizeLogin(login);
        var key = "client:" + normalized;
        if (throttle.Check(key) is { } locked) return new LoginResult(false, locked);

        var client = await db.Clients.FirstOrDefaultAsync(c => c.Login == normalized);

        // Немає такого клієнта — можливо, це адміністратор: йому відкривається службовий сеанс
        if (client == null && await db.Admins.FirstOrDefaultAsync(a => a.Login == normalized) is { } admin
            && !string.IsNullOrEmpty(password) && Passwords.Verify(admin.PasswordHash, password))
        {
            throttle.Success(key);
            return await StartServiceSessionAsync(pcId, admin.Login);
        }

        if (client == null || string.IsNullOrEmpty(password) || !Passwords.Verify(client.PasswordHash, password))
        {
            throttle.Fail(key);
            db.Log(pcId, $"Невдала спроба входу клієнта «{login}»");
            await db.SaveChangesAsync();
            return new LoginResult(false, "Невірний логін або пароль");
        }
        throttle.Success(key);

        if (client.IsBlocked) return new LoginResult(false, "Обліковий запис заблоковано. Зверніться до адміністратора");
        if (client.BalanceMinutes <= 0) return new LoginResult(false, "На балансі немає хвилин. Зверніться до адміністратора");
        if (await GetActiveAsync(pcId) is { } active)
        {
            // Свій призупинений сеанс клієнт може відновити, чужий — ні
            if (active.LockedAt != null && active.ClientId == client.Id)
                return await ResumeAsync(active, client.Login);
            return new LoginResult(false, "На цьому ПК уже йде сеанс");
        }
        if (await db.Sessions.AnyAsync(s => s.ClientId == client.Id && s.ClosedAt == null))
            return new LoginResult(false, "Ви вже працюєте на іншому ПК");

        var now = DateTime.UtcNow;
        var session = new Session
        {
            PcId = pcId,
            ClientId = client.Id,
            StartedAt = now,
            EndsAt = now.AddMinutes(client.BalanceMinutes)  // сеанс триває, доки є баланс
        };
        db.Sessions.Add(session);
        db.Log(pcId, $"Клієнт увійшов, баланс {client.BalanceMinutes} хв", client.Login);
        await db.SaveChangesAsync();

        await commander.SendAsync(pcId, new AgentCommand(CommandType.Unlock, SessionEndsAt: session.EndsAt, Kind: SessionKind.Client));
        return new LoginResult(true);
    }

    /// <summary>
    /// Службовий сеанс адміністратора на клієнтському ПК (обслуговування, встановлення ігор):
    /// без таймера, без контролю програм, без списання хвилин. Обмеження — 24 год про всяк випадок.
    /// </summary>
    private async Task<LoginResult> StartServiceSessionAsync(int pcId, string adminLogin)
    {
        if (await GetActiveAsync(pcId) is { } active)
        {
            if (active.LockedAt == null) return new LoginResult(false, "На цьому ПК уже йде сеанс");

            // Свій призупинений службовий сеанс — просто відновлюємо
            if (active.ServiceAdmin == adminLogin) return await ResumeAsync(active, adminLogin);

            // Чужий призупинений сеанс адміністратор може закрити, щоб сісти за ПК сам:
            // клієнту списується час лише до моменту блокування
            await EndAsync(pcId, $"Призупинений сеанс закрито: за ПК сів адміністратор «{adminLogin}»", adminLogin);
        }

        var now = DateTime.UtcNow;
        var session = new Session
        {
            PcId = pcId,
            ServiceAdmin = adminLogin,
            StartedAt = now,
            EndsAt = now.AddHours(24),
            WarningSent = true          // попередження "залишилось 5 хв" не потрібне
        };
        db.Sessions.Add(session);
        db.Log(pcId, "Адміністратор увійшов на ПК: службовий сеанс без обмежень", adminLogin);
        await db.SaveChangesAsync();

        await commander.SendAsync(pcId, new AgentCommand(CommandType.Unlock, SessionEndsAt: session.EndsAt, Kind: SessionKind.Service));
        return new LoginResult(true);
    }

    /// <summary>
    /// Призупинити сеанс (адміністратор заблокував ПК). Поки сеанс призупинено,
    /// його час не йде; відновити може той самий клієнт/адміністратор або адміністратор з панелі.
    /// </summary>
    public async Task<string?> PauseAsync(int pcId, string? actor)
    {
        var session = await GetActiveAsync(pcId);
        if (session == null) return "Немає активного сеансу";
        if (session.LockedAt != null) return null;   // уже призупинено

        session.LockedAt = DateTime.UtcNow;
        db.Log(pcId, "Сеанс призупинено: ПК заблоковано адміністратором", actor);
        await db.SaveChangesAsync();

        var note = KindOf(session) == SessionKind.Guest
            ? "Сеанс призупинено адміністратором."
            : "Сеанс призупинено адміністратором. Щоб продовжити, увійдіть у свій обліковий запис.";
        await commander.SendAsync(pcId, new AgentCommand(CommandType.Lock, Text: note));
        return null;
    }

    /// <summary>Відновити призупинений сеанс з панелі (кнопка «Розблок»).</summary>
    public async Task<string?> ResumeFromPanelAsync(int pcId, string? actor)
    {
        var session = await GetActiveAsync(pcId);
        if (session?.LockedAt == null) return "Немає призупиненого сеансу";
        var result = await ResumeAsync(session, actor);
        return result.Ok ? null : result.Error;
    }

    private async Task<LoginResult> ResumeAsync(Session session, string? actor)
    {
        var paused = DateTime.UtcNow - session.LockedAt!.Value;
        session.PausedSeconds += (int)paused.TotalSeconds;
        session.EndsAt += paused;                 // час призупинення не забирається в клієнта
        session.LockedAt = null;
        if (KindOf(session) != SessionKind.Service && session.EndsAt - DateTime.UtcNow > TimeSpan.FromMinutes(WarningMinutes))
            session.WarningSent = false;
        db.Log(session.PcId, $"Сеанс відновлено після призупинення ({(int)paused.TotalMinutes} хв)", actor);
        await db.SaveChangesAsync();

        await commander.SendAsync(session.PcId, new AgentCommand(CommandType.Unlock, SessionEndsAt: session.EndsAt, Kind: KindOf(session)));
        return new LoginResult(true);
    }

    /// <summary>Додати час гостьовому сеансу.</summary>
    public async Task<string?> ExtendAsync(int pcId, int minutes, string? actor)
    {
        if (minutes is < 1 or > 1440) return "Кількість хвилин має бути від 1 до 1440";
        var session = await GetActiveAsync(pcId);
        if (session == null) return "Немає активного сеансу";
        if (session.ClientId != null) return "Це сеанс клієнта — поповніть його баланс у розділі «Клієнти»";
        if (session.ServiceAdmin != null) return "Службовий сеанс не обмежений у часі";

        await AddTimeAsync(session, minutes);
        db.Log(pcId, $"Додано {minutes} хв", actor);
        await db.SaveChangesAsync();
        return null;
    }

    /// <summary>Поповнити баланс клієнта. Якщо він зараз грає — сеанс подовжується.</summary>
    public async Task<string?> TopUpAsync(int clientId, int minutes, string? actor)
    {
        if (minutes is < 1 or > 10000) return "Кількість хвилин має бути від 1 до 10000";
        var client = await db.Clients.FindAsync(clientId);
        if (client == null) return "Клієнта не знайдено";

        client.BalanceMinutes += minutes;
        db.TopUps.Add(new TopUp { ClientId = clientId, Minutes = minutes, At = DateTime.UtcNow, Actor = actor });
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.ClientId == clientId && s.ClosedAt == null);
        if (session != null) await AddTimeAsync(session, minutes);

        db.Log(session?.PcId, $"Баланс клієнта «{client.Login}» поповнено на {minutes} хв (тепер {client.BalanceMinutes})", actor);
        await db.SaveChangesAsync();
        return null;
    }

    private async Task AddTimeAsync(Session session, int minutes)
    {
        session.EndsAt = session.EndsAt.AddMinutes(minutes);
        if (session.EndsAt - DateTime.UtcNow > TimeSpan.FromMinutes(WarningMinutes))
            session.WarningSent = false; // попередимо знову ближче до нового кінця
        await commander.SendAsync(session.PcId, new AgentCommand(CommandType.SessionUpdate, SessionEndsAt: session.EndsAt, Kind: KindOf(session)));
    }

    /// <summary>
    /// Закрити всі активні сеанси (вимкнення або запуск сервера).
    /// atLastSeen = true — сеанс вважається завершеним у момент останнього сигналу від ПК,
    /// щоб клієнтам не списувався час, коли сервер не працював.
    /// </summary>
    public async Task CloseAllAsync(string reason, bool atLastSeen)
    {
        var pcIds = await db.Sessions.Where(s => s.ClosedAt == null).Select(s => s.PcId).ToListAsync();
        foreach (var pcId in pcIds)
        {
            DateTime? closedAt = null;
            if (atLastSeen && await db.Pcs.FindAsync(pcId) is { } pc) closedAt = pc.LastSeen;
            await EndAsync(pcId, reason, null, closedAt);
        }
    }

    /// <summary>Завершити сеанс; для клієнта — списати використані хвилини.</summary>
    public async Task<string?> EndAsync(int pcId, string reason, string? actor = null, DateTime? closedAt = null)
    {
        var session = await GetActiveAsync(pcId);
        if (session == null) return "Немає активного сеансу";

        // closedAt задається, коли сеанс закривається "заднім числом" (після збою сервера)
        var now = closedAt ?? DateTime.UtcNow;
        if (now < session.StartedAt) now = session.StartedAt;
        session.ClosedAt = now;
        session.ClosedReason = reason;

        var text = session.ServiceAdmin != null
            ? $"Службовий сеанс адміністратора «{session.ServiceAdmin}» завершено: {reason}"
            : $"Сеанс завершено: {reason}";
        if (session.ClientId != null && await db.Clients.FindAsync(session.ClientId.Value) is { } client)
        {
            // Оплачується лише час, коли сеанс не був призупинений. Якщо сеанс завершується
            // в стані призупинення — час рахується до моменту блокування.
            var paidEnd = session.LockedAt is { } lockedAt && lockedAt < now ? lockedAt : now;
            var paidSeconds = (paidEnd - session.StartedAt).TotalSeconds - session.PausedSeconds;
            // Хвилини рахуються з округленням угору, але не більше, ніж було на балансі
            var used = (int)Math.Ceiling(Math.Max(0, paidSeconds) / 60);
            used = Math.Clamp(used, 0, client.BalanceMinutes);
            client.BalanceMinutes -= used;
            session.ChargedMinutes = used;
            text += $". Клієнт «{client.Login}»: списано {used} хв, залишок {client.BalanceMinutes} хв";
        }

        db.Log(pcId, text, actor);
        await db.SaveChangesAsync();

        await commander.SendAsync(pcId, new AgentCommand(CommandType.Lock, Text: reason));
        return null;
    }
}
