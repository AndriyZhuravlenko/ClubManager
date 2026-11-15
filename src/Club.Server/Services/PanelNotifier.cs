using Club.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Club.Server.Services;

/// <summary>
/// Надсилає відкритим панелям адміністратора сповіщення про зміни (push замість опитування).
/// Зміни за короткий проміжок часу об'єднуються в одне повідомлення: якщо за 300 мс
/// збереглося 10 записів у базі, панель отримає одне сповіщення, а не десять.
/// </summary>
public class PanelNotifier(IHubContext<PanelHub> hub, ILogger<PanelNotifier> log)
{
    private static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(300);

    private readonly object _lock = new();
    private readonly HashSet<string> _pending = [];
    private bool _scheduled;
    private long _sent;

    /// <summary>Скільки сповіщень надіслано з моменту запуску сервера.</summary>
    public long Sent => Interlocked.Read(ref _sent);

    public void Notify(IEnumerable<string> topics)
    {
        lock (_lock)
        {
            foreach (var t in topics) _pending.Add(t);
            if (_pending.Count == 0 || _scheduled) return;
            _scheduled = true;
        }
        _ = FlushLaterAsync();
    }

    public void Notify(params string[] topics) => Notify((IEnumerable<string>)topics);

    private async Task FlushLaterAsync()
    {
        await Task.Delay(Coalesce);
        string[] topics;
        lock (_lock)
        {
            topics = [.. _pending];
            _pending.Clear();
            _scheduled = false;
        }
        try
        {
            await hub.Clients.All.SendAsync(PanelHub.Changed, topics);
            Interlocked.Increment(ref _sent);
        }
        catch (Exception ex)
        {
            log.LogDebug("Не вдалося сповістити панель: {Message}", ex.Message);
        }
    }
}
