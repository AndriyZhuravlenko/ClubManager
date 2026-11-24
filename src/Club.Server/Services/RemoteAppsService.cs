using Club.Server.Hubs;
using Club.Shared;
using Microsoft.AspNetCore.SignalR;

namespace Club.Server.Services;

public class RemoteAppsException(string message) : Exception(message);

/// <summary>
/// Керування програмами на ПК під час сеансу: сервер надсилає агенту запит і чекає відповіді
/// (SignalR client results). Агент виконує його в сеансі користувача Windows.
/// </summary>
public class RemoteAppsService(IHubContext<AgentHub> hub, ConnectionRegistry registry, ILogger<RemoteAppsService> log)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public Task<List<RunningApp>> ListAsync(int pcId, CancellationToken ct = default) =>
        InvokeAsync<List<RunningApp>>(pcId, HubMethods.ListApps, null, ct);

    public Task<AppActionResult> CloseAsync(int pcId, string path, CancellationToken ct = default) =>
        InvokeAsync<AppActionResult>(pcId, HubMethods.CloseApp, path, ct);

    public Task<AppActionResult> LaunchAsync(int pcId, string path, CancellationToken ct = default) =>
        InvokeAsync<AppActionResult>(pcId, HubMethods.LaunchApp, path, ct);

    private async Task<T> InvokeAsync<T>(int pcId, string method, string? argument, CancellationToken ct)
    {
        if (!registry.TryGetConnection(pcId, out var connection))
            throw new RemoteAppsException("ПК не в мережі");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            var client = hub.Clients.Client(connection);   // ISingleClientProxy — дозволяє отримати відповідь
            return argument == null
                ? await client.InvokeAsync<T>(method, timeout.Token)
                : await client.InvokeAsync<T>(method, argument, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RemoteAppsException("ПК не відповів вчасно");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Найчастіше — агент старої версії, який ще не вміє керувати програмами
            log.LogWarning("Запит {Method} до ПК {PcId} не вдався: {Message}", method, pcId, ex.Message);
            throw new RemoteAppsException("ПК не виконав запит — можливо, на ньому стара версія агента (оновіть агента)");
        }
    }
}
