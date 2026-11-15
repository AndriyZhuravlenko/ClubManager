using Microsoft.EntityFrameworkCore;

namespace Club.Server.Services;

/// <summary>
/// Обслуговування бази даних (раз на 6 годин, перший раз — через хвилину після запуску):
///  - видаляє записи журналу подій, старші за EventRetentionDays (за замовчуванням 180 днів;
///    0 — не видаляти). Сеанси й поповнення не видаляються — вони потрібні для звітів;
///  - стискає службовий файл SQLite (club.db-wal), який інакше лише росте.
/// </summary>
public class MaintenanceService(IServiceScopeFactory scopes, IConfiguration config, ILogger<MaintenanceService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
            do
            {
                try { await RunAsync(ct); }
                catch (Exception ex) { log.LogWarning("Помилка обслуговування бази: {Message}", ex.Message); }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();

        var days = config.GetValue("EventRetentionDays", 180);
        if (days > 0)
        {
            var before = DateTime.UtcNow.AddDays(-days);
            var deleted = await db.Events.Where(e => e.At < before).ExecuteDeleteAsync(ct);
            if (deleted > 0)
            {
                db.Log(null, $"Обслуговування: видалено записів журналу, старших за {days} днів: {deleted}");
                await db.SaveChangesAsync(ct);
            }
        }

        // Перенести зміни з журналу SQLite (WAL) в основний файл і обрізати його
        await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", ct);
    }
}
