using System.Collections.Concurrent;
using System.Diagnostics;
using Club.Server.Hubs;
using Club.Shared;
using Microsoft.AspNetCore.SignalR;

namespace Club.Server.Services;

/// <summary>
/// Надсилає команди конкретному ПК через SignalR і запам'ятовує момент відправлення,
/// щоб виміряти час доставки й виконання команди (від сервера до підтвердження агента).
/// </summary>
public class AgentCommander(IHubContext<AgentHub> hub, ConnectionRegistry registry)
{
    private readonly ConcurrentDictionary<(int PcId, CommandType Type), long> _sentAt = new();

    /// <returns>false — якщо ПК зараз не в мережі.</returns>
    public async Task<bool> SendAsync(int pcId, AgentCommand command)
    {
        if (!registry.TryGetConnection(pcId, out var connectionId)) return false;
        _sentAt[(pcId, command.Type)] = Stopwatch.GetTimestamp();
        await hub.Clients.Client(connectionId).SendAsync(HubMethods.Command, command);
        return true;
    }

    /// <summary>Скільки мілісекунд минуло від відправлення команди до підтвердження (або null).</summary>
    public double? TakeRoundTripMs(int pcId, CommandType type) =>
        _sentAt.TryRemove((pcId, type), out var sent)
            ? Stopwatch.GetElapsedTime(sent).TotalMilliseconds
            : null;
}
