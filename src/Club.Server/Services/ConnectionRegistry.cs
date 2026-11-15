using System.Collections.Concurrent;

namespace Club.Server.Services;

/// <summary>
/// Пам'ятає, який ПК через яке SignalR-з'єднання підключений.
/// Якщо ПК є в реєстрі — він онлайн.
/// </summary>
public class ConnectionRegistry
{
    private readonly ConcurrentDictionary<int, string> _byPc = new();
    private readonly ConcurrentDictionary<string, int> _byConnection = new();

    public void Add(int pcId, string connectionId)
    {
        _byPc[pcId] = connectionId;
        _byConnection[connectionId] = pcId;
    }

    /// <summary>Видаляє з'єднання і повертає Id ПК, якому воно належало.</summary>
    public int? RemoveByConnection(string connectionId)
    {
        if (!_byConnection.TryRemove(connectionId, out var pcId)) return null;
        // Видаляємо ПК, лише якщо в нього не з'явилося новіше з'єднання
        _byPc.TryRemove(new KeyValuePair<int, string>(pcId, connectionId));
        return pcId;
    }

    public bool TryGetConnection(int pcId, out string connectionId) =>
        _byPc.TryGetValue(pcId, out connectionId!);

    public bool IsOnline(int pcId) => _byPc.ContainsKey(pcId);

    // Останні показники навантаження агента (надходять разом із heartbeat)
    private readonly ConcurrentDictionary<int, AgentMetrics> _metrics = new();

    public void SetMetrics(int pcId, AgentMetrics metrics) => _metrics[pcId] = metrics;

    public AgentMetrics? GetMetrics(int pcId) => _metrics.TryGetValue(pcId, out var m) ? m : null;

    // Час останнього heartbeat, ще не записаний у базу (записує пакетом HeartbeatFlusher)
    private readonly ConcurrentDictionary<int, DateTime> _pendingSeen = new();

    public void Touch(int pcId) => _pendingSeen[pcId] = DateTime.UtcNow;

    /// <summary>Забрати всі накопичені позначки «ПК живий» для запису в базу.</summary>
    public Dictionary<int, DateTime> TakePendingSeen()
    {
        var result = new Dictionary<int, DateTime>();
        foreach (var pcId in _pendingSeen.Keys)
            if (_pendingSeen.TryRemove(pcId, out var at)) result[pcId] = at;
        return result;
    }
}

/// <summary>Навантаження, яке створює агент на ПК.</summary>
public record AgentMetrics(double CpuPercent, double MemoryMb, DateTime At);
