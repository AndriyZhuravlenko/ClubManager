using Club.Server.Data;
using Club.Server.Services;

namespace Club.Tests;

/// <summary>Пакетний запис heartbeat: сигнали накопичуються в пам'яті й записуються в базу разом.</summary>
public sealed class HeartbeatFlusherTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly AppDb _db;
    private readonly DateTime _old = DateTime.UtcNow.AddMinutes(-10);

    public HeartbeatFlusherTests()
    {
        _db = _database.Create();
        for (var i = 1; i <= 3; i++)
            _db.Pcs.Add(new Pc { MachineName = $"PC-{i}", DisplayName = $"ПК-{i}", LastSeen = _old });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _database.Dispose();
    }

    [Fact]
    public void Registry_keeps_only_latest_signal_and_is_emptied_after_take()
    {
        var registry = new ConnectionRegistry();
        registry.Touch(1);
        registry.Touch(1);
        registry.Touch(2);

        var pending = registry.TakePendingSeen();

        Assert.Equal(2, pending.Count);                 // по одному запису на ПК, а не на кожен сигнал
        Assert.Empty(registry.TakePendingSeen());       // повторно нічого не записується
    }

    [Fact]
    public async Task All_pcs_are_written_in_one_batch()
    {
        var ids = _db.Pcs.Select(p => p.Id).ToList();
        var now = DateTime.UtcNow;

        var updated = await HeartbeatFlusher.FlushAsync(_db, ids.ToDictionary(id => id, _ => now));

        Assert.Equal(3, updated);
        using var check = _database.Create();             // читаємо з бази, а не з кешу контексту
        Assert.All(check.Pcs.ToList(), p => Assert.True(p.LastSeen > _old));
    }

    [Fact]
    public async Task Newer_time_in_database_is_not_overwritten()
    {
        var pc = _db.Pcs.First();
        var disconnectedAt = DateTime.UtcNow;
        pc.LastSeen = disconnectedAt;                     // момент відключення, записаний окремо
        _db.SaveChanges();

        var updated = await HeartbeatFlusher.FlushAsync(_db, new Dictionary<int, DateTime> { [pc.Id] = disconnectedAt.AddSeconds(-3) });

        Assert.Equal(0, updated);
        Assert.Equal(disconnectedAt, pc.LastSeen);
    }

    [Fact]
    public async Task Deleted_pc_is_skipped()
    {
        var updated = await HeartbeatFlusher.FlushAsync(_db, new Dictionary<int, DateTime> { [9999] = DateTime.UtcNow });
        Assert.Equal(0, updated);
    }
}
