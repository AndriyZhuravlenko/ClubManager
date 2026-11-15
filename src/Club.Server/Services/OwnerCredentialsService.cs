using Club.Server.Data;
using Club.Server.Hubs;
using Club.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Club.Server.Services;

/// <summary>
/// Розсилає агентам хеші паролів власників, щоб власник міг увійти на клієнтський ПК,
/// навіть коли сервер недоступний. Надсилається лише PBKDF2-хеш, не пароль.
/// </summary>
public class OwnerCredentialsService(AppDb db, IHubContext<AgentHub> hub)
{
    public Task<List<OwnerCredential>> GetAsync() =>
        db.Admins.Where(a => a.Role == Roles.Owner)
            .Select(a => new OwnerCredential(a.Login, a.PasswordHash))
            .ToListAsync();

    /// <summary>Надіслати актуальний список усім зареєстрованим агентам.</summary>
    public async Task BroadcastAsync() =>
        await hub.Clients.Group(AgentHub.AgentsGroup).SendAsync(HubMethods.OwnerCredentials, await GetAsync());
}
