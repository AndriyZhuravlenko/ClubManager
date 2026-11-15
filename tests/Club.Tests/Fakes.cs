using System.Collections.Concurrent;
using Club.Server.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Club.Tests;

/// <summary>Одне надіслане через SignalR повідомлення: кому, який метод, з якими даними.</summary>
public record SentMessage(string Target, string Method, object?[] Args);

/// <summary>
/// Підробний контекст SignalR-хабу: замість реальної мережі записує всі повідомлення в список,
/// щоб тест міг перевірити, яку команду сервер надіслав агенту чи панелі.
/// </summary>
public class FakeHubContext<THub> : IHubContext<THub> where THub : Hub
{
    public ConcurrentQueue<SentMessage> Sent { get; } = new();
    public IHubClients Clients { get; }
    public IGroupManager Groups { get; } = new FakeGroups();

    public FakeHubContext() => Clients = new FakeClients(this);

    private class FakeClients(FakeHubContext<THub> owner) : IHubClients
    {
        private IClientProxy Proxy(string target) => new FakeProxy(owner, target);
        public IClientProxy All => Proxy("all");
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy("all-except");
        public IClientProxy Client(string connectionId) => Proxy(connectionId);
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy(string.Join(",", connectionIds));
        public IClientProxy Group(string groupName) => Proxy("group:" + groupName);
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy("group:" + groupName);
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy("groups:" + string.Join(",", groupNames));
        public IClientProxy User(string userId) => Proxy("user:" + userId);
        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy("users:" + string.Join(",", userIds));
    }

    private class FakeProxy(FakeHubContext<THub> owner, string target) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            owner.Sent.Enqueue(new SentMessage(target, method, args));
            return Task.CompletedTask;
        }
    }

    private class FakeGroups : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

/// <summary>
/// Справжня база SQLite, але в оперативній пам'яті: кожен тест отримує чисту базу
/// з тією самою структурою, що й на сервері, і нічого не записує на диск.
/// </summary>
public sealed class TestDatabase : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DbContextOptions<AppDb> _options;

    public TestDatabase()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDb>().UseSqlite(_connection).Options;
        using var db = Create();
        db.Database.EnsureCreated();
    }

    /// <summary>Новий контекст до тієї самої бази (як новий HTTP-запит на сервері).</summary>
    public AppDb Create(Club.Server.Services.PanelNotifier? notifier = null) => new(_options, notifier);

    public void Dispose() => _connection.Dispose();
}
