using Club.Server.Data;
using Club.Shared;
using Microsoft.EntityFrameworkCore;

namespace Club.Server.Services;

/// <summary>
/// Фонова служба: кожні 5 секунд перевіряє сеанси.
/// - попереджає користувача за 5 хв до кінця;
/// - завершує сеанси, час яких вичерпано (ПК блокується);
/// - завершує призупинені сеанси, які ніхто не відновив за 30 хв;
/// - завершує сеанси на ПК, які зникли з мережі (вимкнені, зависли) і не повернулися
///   протягом OfflineGrace; час списується лише до моменту зникнення ПК.
/// </summary>
public class SessionWatcher(IServiceScopeFactory scopes, ConnectionRegistry registry, ILogger<SessionWatcher> log) : BackgroundService
{
    // Призупинений сеанс, який ніхто не відновив, завершується через цей час
    private static readonly TimeSpan PausedLimit = TimeSpan.FromMinutes(30);

    // Більше, ніж затримка блокування на самому агенті (30 с): короткий збій мережі сеанс не перериває
    private static readonly TimeSpan OfflineGrace = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await TickAsync(); }
            catch (Exception ex) { log.LogError(ex, "Помилка перевірки сеансів"); }
        }
    }

    private async Task TickAsync()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
        var commander = scope.ServiceProvider.GetRequiredService<AgentCommander>();

        var now = DateTime.UtcNow;

        // 1. Завершуємо прострочені сеанси
        var expired = await db.Sessions
            .Where(s => s.ClosedAt == null && s.LockedAt == null && s.EndsAt <= now)
            .Select(s => s.PcId)
            .ToListAsync();
        foreach (var pcId in expired)
            await sessions.EndAsync(pcId, "Час вичерпано");

        // 2. Завершуємо сеанси на ПК, які зникли з мережі й не повернулися
        var activePcIds = await db.Sessions.Where(s => s.ClosedAt == null).Select(s => s.PcId).ToListAsync();
        foreach (var pcId in activePcIds.Where(id => !registry.IsOnline(id)))
        {
            var pc = await db.Pcs.FindAsync(pcId);
            if (pc == null || now - pc.LastSeen < OfflineGrace) continue;
            await sessions.EndAsync(pcId, "ПК вимкнувся або втратив зв'язок", closedAt: pc.LastSeen);
        }

        // 3. Призупинені сеанси, які ніхто не відновив, — завершуємо (час рахується до блокування)
        var pausedBefore = now - PausedLimit;
        var stalePaused = await db.Sessions
            .Where(s => s.ClosedAt == null && s.LockedAt != null && s.LockedAt <= pausedBefore)
            .Select(s => new { s.PcId, s.LockedAt })
            .ToListAsync();
        foreach (var s in stalePaused)
            await sessions.EndAsync(s.PcId, "Призупинений сеанс не відновлено протягом 30 хв", closedAt: s.LockedAt);

        // 4. Попереджаємо тих, у кого залишилось менше 5 хвилин
        var warnBefore = now.AddMinutes(SessionService.WarningMinutes);
        var toWarn = await db.Sessions
            .Where(s => s.ClosedAt == null && s.LockedAt == null && !s.WarningSent && s.EndsAt <= warnBefore)
            .ToListAsync();
        foreach (var s in toWarn)
        {
            var left = Math.Max(1, (int)Math.Ceiling((s.EndsAt - now).TotalMinutes));
            await commander.SendAsync(s.PcId, new AgentCommand(CommandType.ShowMessage,
                Text: $"До кінця сеансу залишилось {left} хв"));
            s.WarningSent = true;
        }
        if (toWarn.Count > 0) await db.SaveChangesAsync();
    }
}
