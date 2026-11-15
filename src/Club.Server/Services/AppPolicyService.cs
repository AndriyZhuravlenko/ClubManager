using Club.Server.Data;
using Club.Server.Hubs;
using Club.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Club.Server.Services;

/// <summary>
/// Політика доступу до програм: каталог дозволених програм + режим контролю.
/// Після будь-якої зміни політика одразу розсилається всім підключеним агентам.
/// </summary>
public class AppPolicyService(AppDb db, IHubContext<AgentHub> hub)
{
    public const string EnforceKey = "AppControl.Enforce";

    public async Task<AppPolicy> GetAsync()
    {
        var apps = await db.Apps.OrderBy(a => a.Name)
            .Select(a => new AppInfo(a.Id, a.Name, a.Path)).ToListAsync();
        var enforce = (await db.Settings.FindAsync(EnforceKey))?.Value == "true";
        return new AppPolicy(enforce, apps);
    }

    public async Task SetEnforceAsync(bool enforce)
    {
        var setting = await db.Settings.FindAsync(EnforceKey);
        if (setting == null) db.Settings.Add(new Setting { Key = EnforceKey, Value = enforce ? "true" : "false" });
        else setting.Value = enforce ? "true" : "false";
        await db.SaveChangesAsync();
    }

    /// <summary>Надіслати актуальну політику всім агентам.</summary>
    public async Task BroadcastAsync() =>
        await hub.Clients.Group(AgentHub.AgentsGroup).SendAsync(HubMethods.AppPolicy, await GetAsync());
}
