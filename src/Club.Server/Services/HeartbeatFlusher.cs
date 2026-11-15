using Club.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Club.Server.Services;

/// <summary>
/// Пакетний запис heartbeat у базу. Агенти надсилають сигнал «я живий» кожні 15 с;
/// замість окремого запису на кожен сигнал час накопичується в пам'яті (ConnectionRegistry)
/// і раз на 5 с записується для всіх ПК однією транзакцією.
///
/// Навантажувальний тест (200 ПК, сигнал щосекунди) показав, що без цього SQLite
/// обробляв лише ~89 записів/с, а запити стояли в черзі до кількох секунд.
/// </summary>
public class HeartbeatFlusher(IServiceScopeFactory scopes, ConnectionRegistry registry, ILogger<HeartbeatFlusher> log)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) await FlushOnceAsync();
        }
        catch (OperationCanceledException) { }
        await FlushOnceAsync();   // під час зупинки сервера — записати останнє
    }

    private async Task FlushOnceAsync()
    {
        var pending = registry.TakePendingSeen();
        if (pending.Count == 0) return;
        try
        {
            using var scope = scopes.CreateScope();
            await FlushAsync(scope.ServiceProvider.GetRequiredService<AppDb>(), pending);
        }
        catch (Exception ex)
        {
            log.LogWarning("Не вдалося записати heartbeat у базу: {Message}", ex.Message);
        }
    }

    /// <summary>Записати час останнього сигналу для кількох ПК однією транзакцією. Повертає кількість оновлених ПК.</summary>
    public static async Task<int> FlushAsync(AppDb db, IReadOnlyDictionary<int, DateTime> pending)
    {
        var ids = pending.Keys.ToList();
        var pcs = await db.Pcs.Where(p => ids.Contains(p.Id)).ToListAsync();
        var updated = 0;
        foreach (var pc in pcs)
        {
            // Не переписуємо новіший час (напр., момент відключення, записаний окремо)
            if (pending[pc.Id] > pc.LastSeen) { pc.LastSeen = pending[pc.Id]; updated++; }
        }
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Один із ПК щойно видалили з панелі — для нього записувати нічого
        }
        return updated;
    }
}
