using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Club.Server.Hubs;

/// <summary>
/// Хаб для панелі адміністратора. Панель нічого не викликає на сервері через нього —
/// лише отримує повідомлення "Changed" зі списком розділів, що змінилися
/// ("pcs", "clients", "apps", "events", "admins"), і перезавантажує тільки їх.
/// Так панель оновлюється миттєво і не опитує сервер кожні 2 секунди.
/// </summary>
[Authorize]
public class PanelHub : Hub
{
    public const string Changed = "Changed";
}
