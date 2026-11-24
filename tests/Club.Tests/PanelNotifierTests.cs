using Club.Server.Data;
using Club.Server.Hubs;
using Club.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Club.Tests;

/// <summary>Push-оновлення панелі: об'єднання змін і вибір розділів, які треба оновити.</summary>
public class PanelNotifierTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(700);   // більше за 300 мс об'єднання

    [Fact]
    public async Task Many_changes_are_coalesced_into_one_message()
    {
        var hub = new FakeHubContext<PanelHub>();
        var notifier = new PanelNotifier(hub, NullLogger<PanelNotifier>.Instance);

        for (var i = 0; i < 50; i++) notifier.Notify("pcs");
        notifier.Notify("events");
        await Task.Delay(Wait);

        var message = Assert.Single(hub.Sent);
        Assert.Equal(PanelHub.Changed, message.Method);
        Assert.Equal(new[] { "events", "pcs" }, ((string[])message.Args[0]!).Order());
    }

    [Fact]
    public async Task Later_changes_produce_a_new_message()
    {
        var hub = new FakeHubContext<PanelHub>();
        var notifier = new PanelNotifier(hub, NullLogger<PanelNotifier>.Instance);

        notifier.Notify("pcs");
        await Task.Delay(Wait);
        notifier.Notify("clients");
        await Task.Delay(Wait);

        Assert.Equal(2, hub.Sent.Count);
        Assert.Equal(2, notifier.Sent);
    }

    [Fact]
    public async Task Saving_to_database_notifies_only_changed_sections()
    {
        using var database = new TestDatabase();
        var hub = new FakeHubContext<PanelHub>();
        var notifier = new PanelNotifier(hub, NullLogger<PanelNotifier>.Instance);
        using var db = database.Create(notifier);

        db.Clients.Add(new Client { Login = "x", PasswordHash = "h", CreatedAt = DateTime.UtcNow });
        db.Log(null, "тест");
        await db.SaveChangesAsync();
        await Task.Delay(Wait);

        var topics = (string[])Assert.Single(hub.Sent).Args[0]!;
        Assert.Equal(new[] { "clients", "events" }, topics.Order());
    }

    [Fact]
    public async Task New_chat_message_updates_tiles_and_open_chat()
    {
        using var database = new TestDatabase();
        var hub = new FakeHubContext<PanelHub>();
        using var db = database.Create(new PanelNotifier(hub, NullLogger<PanelNotifier>.Instance));

        db.Chats.Add(new ChatMessage { PcId = 1, SessionId = 1, Author = "ivan", Text = "допоможіть", At = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await Task.Delay(Wait);

        var topics = (string[])Assert.Single(hub.Sent).Args[0]!;
        Assert.Equal(new[] { "chat", "pcs" }, topics.Order());
    }

    [Fact]
    public async Task Nothing_is_sent_when_nothing_changed()
    {
        using var database = new TestDatabase();
        var hub = new FakeHubContext<PanelHub>();
        using var db = database.Create(new PanelNotifier(hub, NullLogger<PanelNotifier>.Instance));

        await db.SaveChangesAsync();
        await Task.Delay(Wait);

        Assert.Empty(hub.Sent);
    }
}
