using Club.Server.Data;
using Club.Server.Hubs;
using Club.Server.Security;
using Club.Server.Services;
using Club.Shared;
using Microsoft.EntityFrameworkCore;

namespace Club.Tests;

/// <summary>
/// Логіка сеансів на справжній базі SQLite (у пам'яті): відкриття, списання хвилин,
/// призупинення, службові сеанси. Агенти підмінено записувачем команд.
/// </summary>
public sealed class SessionServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly AppDb _db;
    private readonly FakeHubContext<AgentHub> _hub = new();
    private readonly SessionService _sessions;
    private readonly int _pc1, _pc2;

    public SessionServiceTests()
    {
        _db = _database.Create();
        var p1 = new Pc { MachineName = "PC-1", DisplayName = "ПК-1", LastSeen = DateTime.UtcNow };
        var p2 = new Pc { MachineName = "PC-2", DisplayName = "ПК-2", LastSeen = DateTime.UtcNow };
        _db.Pcs.AddRange(p1, p2);
        _db.Clients.Add(new Client { Login = "ivan", PasswordHash = Passwords.Hash("1111"), BalanceMinutes = 60, CreatedAt = DateTime.UtcNow });
        _db.Admins.Add(new AdminUser { Login = "boss", PasswordHash = Passwords.Hash("2222"), Role = Roles.Owner, CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();
        _pc1 = p1.Id; _pc2 = p2.Id;

        // Обидва ПК "у мережі": команди для них записуються у _hub.Sent
        var registry = new ConnectionRegistry();
        registry.Add(_pc1, "conn-1");
        registry.Add(_pc2, "conn-2");
        _sessions = new SessionService(_db, new AgentCommander(_hub, registry), new LoginThrottle());
    }

    public void Dispose()
    {
        _db.Dispose();
        _database.Dispose();
    }

    // --- допоміжне ---

    private AgentCommand LastCommandTo(string connection) =>
        (AgentCommand)_hub.Sent.Last(m => m.Target == connection && m.Method == HubMethods.Command).Args[0]!;

    private Client Ivan => _db.Clients.Single(c => c.Login == "ivan");

    private Session Active(int pcId) => _db.Sessions.Single(s => s.PcId == pcId && s.ClosedAt == null);

    /// <summary>Перенести початок сеансу в минуле, ніби він уже триває кілька хвилин.</summary>
    private void Age(int pcId, TimeSpan by)
    {
        var s = Active(pcId);
        s.StartedAt -= by;
        _db.SaveChanges();
    }

    // Невеликий запас, щоб тест не залежав від мілісекунд виконання (хвилини округлюються вгору)
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(5);

    // --- гостьові сеанси ---

    [Fact]
    public async Task Guest_session_unlocks_pc_for_given_time()
    {
        Assert.Null(await _sessions.StartAsync(_pc1, 60, "admin"));

        var s = Active(_pc1);
        Assert.Null(s.ClientId);
        Assert.InRange((s.EndsAt - DateTime.UtcNow).TotalMinutes, 59.9, 60.1);
        var cmd = LastCommandTo("conn-1");
        Assert.Equal(CommandType.Unlock, cmd.Type);
        Assert.Equal(SessionKind.Guest, cmd.Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1441)]
    public async Task Guest_session_rejects_wrong_duration(int minutes) =>
        Assert.NotNull(await _sessions.StartAsync(_pc1, minutes, "admin"));

    [Fact]
    public async Task Only_one_session_per_pc()
    {
        await _sessions.StartAsync(_pc1, 30, "admin");
        Assert.NotNull(await _sessions.StartAsync(_pc1, 30, "admin"));
    }

    [Fact]
    public async Task Extend_adds_time_to_guest_session()
    {
        await _sessions.StartAsync(_pc1, 30, "admin");
        var before = Active(_pc1).EndsAt;
        Assert.Null(await _sessions.ExtendAsync(_pc1, 15, "admin"));
        Assert.Equal(before.AddMinutes(15), Active(_pc1).EndsAt);
        Assert.Equal(CommandType.SessionUpdate, LastCommandTo("conn-1").Type);
    }

    [Fact]
    public async Task End_locks_pc()
    {
        await _sessions.StartAsync(_pc1, 30, "admin");
        Assert.Null(await _sessions.EndAsync(_pc1, "тест"));
        Assert.Equal(CommandType.Lock, LastCommandTo("conn-1").Type);
        Assert.False(_db.Sessions.Any(s => s.ClosedAt == null));
    }

    // --- клієнтські сеанси і списання хвилин ---

    [Fact]
    public async Task Client_login_opens_session_limited_by_balance()
    {
        var r = await _sessions.ClientLoginAsync(_pc1, "IVAN", "1111");   // логін без урахування регістру
        Assert.True(r.Ok, r.Error);
        var s = Active(_pc1);
        Assert.Equal(Ivan.Id, s.ClientId);
        Assert.InRange((s.EndsAt - DateTime.UtcNow).TotalMinutes, 59.9, 60.1);
        Assert.Equal(SessionKind.Client, LastCommandTo("conn-1").Kind);
    }

    [Fact]
    public async Task Wrong_password_is_rejected_and_logged()
    {
        var r = await _sessions.ClientLoginAsync(_pc1, "ivan", "0000");
        Assert.False(r.Ok);
        Assert.False(_db.Sessions.Any());
        Assert.Contains(_db.Events, e => e.Text.Contains("Невдала спроба входу"));
    }

    [Fact]
    public async Task Brute_force_is_throttled()
    {
        for (var i = 0; i < 5; i++) await _sessions.ClientLoginAsync(_pc1, "ivan", "0000");
        var r = await _sessions.ClientLoginAsync(_pc1, "ivan", "1111");    // навіть правильний пароль
        Assert.False(r.Ok);
        Assert.Contains("Забагато", r.Error);
    }

    [Fact]
    public async Task Blocked_client_cannot_log_in()
    {
        Ivan.IsBlocked = true;
        _db.SaveChanges();
        Assert.False((await _sessions.ClientLoginAsync(_pc1, "ivan", "1111")).Ok);
    }

    [Fact]
    public async Task Client_without_balance_cannot_log_in()
    {
        Ivan.BalanceMinutes = 0;
        _db.SaveChanges();
        Assert.False((await _sessions.ClientLoginAsync(_pc1, "ivan", "1111")).Ok);
    }

    [Fact]
    public async Task Client_cannot_use_two_pcs_at_once()
    {
        Assert.True((await _sessions.ClientLoginAsync(_pc1, "ivan", "1111")).Ok);
        var second = await _sessions.ClientLoginAsync(_pc2, "ivan", "1111");
        Assert.False(second.Ok);
        Assert.Contains("іншому ПК", second.Error);
    }

    [Fact]
    public async Task Used_minutes_are_charged_rounded_up()
    {
        await _sessions.ClientLoginAsync(_pc1, "ivan", "1111");
        Age(_pc1, TimeSpan.FromMinutes(10) - Margin);          // 9 хв 55 с → 10 хв

        await _sessions.EndAsync(_pc1, "тест");

        Assert.Equal(50, Ivan.BalanceMinutes);
        Assert.Equal(10, _db.Sessions.Single().ChargedMinutes);
    }

    [Fact]
    public async Task Charge_never_exceeds_balance()
    {
        await _sessions.ClientLoginAsync(_pc1, "ivan", "1111");
        Age(_pc1, TimeSpan.FromHours(3));                       // сервер "проспав" кінець сеансу

        await _sessions.EndAsync(_pc1, "тест");

        Assert.Equal(0, Ivan.BalanceMinutes);
        Assert.Equal(60, _db.Sessions.Single().ChargedMinutes);
    }

    [Fact]
    public async Task Topup_during_session_extends_it()
    {
        await _sessions.ClientLoginAsync(_pc1, "ivan", "1111");
        var before = Active(_pc1).EndsAt;

        Assert.Null(await _sessions.TopUpAsync(Ivan.Id, 30, "admin"));

        Assert.Equal(90, Ivan.BalanceMinutes);
        Assert.Equal(before.AddMinutes(30), Active(_pc1).EndsAt);
        Assert.Single(_db.TopUps);
    }

    [Fact]
    public async Task Client_session_cannot_be_extended_from_panel()
    {
        await _sessions.ClientLoginAsync(_pc1, "ivan", "1111");
        Assert.NotNull(await _sessions.ExtendAsync(_pc1, 10, "admin"));
    }

    // --- призупинення ---

    [Fact]
    public async Task Paused_time_is_not_charged()
    {
        await _sessions.ClientLoginAsync(_pc1, "ivan", "1111");
        await _sessions.PauseAsync(_pc1, "admin");
        var s = Active(_pc1);
        s.StartedAt = DateTime.UtcNow - TimeSpan.FromMinutes(30) + Margin;   // працював 20 хв,
        s.LockedAt = DateTime.UtcNow - TimeSpan.FromMinutes(10);             // потім 10 хв на паузі
        _db.SaveChanges();

        await _sessions.EndAsync(_pc1, "тест");

        Assert.Equal(20, _db.Sessions.Single().ChargedMinutes);
        Assert.Equal(40, Ivan.BalanceMinutes);
    }

    [Fact]
    public async Task Resume_moves_end_time_by_pause_length()
    {
        await _sessions.StartAsync(_pc1, 60, "admin");
        await _sessions.PauseAsync(_pc1, "admin");
        Assert.Equal(CommandType.Lock, LastCommandTo("conn-1").Type);
        var s = Active(_pc1);
        var endsBefore = s.EndsAt;
        s.LockedAt = DateTime.UtcNow - TimeSpan.FromMinutes(10);
        _db.SaveChanges();

        Assert.Null(await _sessions.ResumeFromPanelAsync(_pc1, "admin"));

        s = Active(_pc1);
        Assert.Null(s.LockedAt);
        Assert.InRange(s.PausedSeconds, 599, 605);
        Assert.InRange((s.EndsAt - endsBefore).TotalMinutes, 9.98, 10.1);
        Assert.Equal(CommandType.Unlock, LastCommandTo("conn-1").Type);
    }

    [Fact]
    public async Task Client_can_resume_own_paused_session_but_not_others()
    {
        _db.Clients.Add(new Client { Login = "petro", PasswordHash = Passwords.Hash("3333"), BalanceMinutes = 60, CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();
        await _sessions.ClientLoginAsync(_pc1, "ivan", "1111");
        await _sessions.PauseAsync(_pc1, "admin");

        Assert.False((await _sessions.ClientLoginAsync(_pc1, "petro", "3333")).Ok);
        Assert.True((await _sessions.ClientLoginAsync(_pc1, "ivan", "1111")).Ok);
        Assert.Null(Active(_pc1).LockedAt);
    }

    // --- службові сеанси адміністратора ---

    [Fact]
    public async Task Admin_login_on_pc_opens_service_session_without_charge()
    {
        var r = await _sessions.ClientLoginAsync(_pc1, "boss", "2222");
        Assert.True(r.Ok, r.Error);
        var s = Active(_pc1);
        Assert.Equal("boss", s.ServiceAdmin);
        Assert.Equal(SessionKind.Service, LastCommandTo("conn-1").Kind);
        Assert.NotNull(await _sessions.ExtendAsync(_pc1, 10, "boss"));   // часу не додається — він не обмежений
    }

    [Fact]
    public async Task Admin_can_take_over_paused_client_session()
    {
        await _sessions.ClientLoginAsync(_pc1, "ivan", "1111");
        await _sessions.PauseAsync(_pc1, "admin");

        Assert.True((await _sessions.ClientLoginAsync(_pc1, "boss", "2222")).Ok);

        Assert.Equal("boss", Active(_pc1).ServiceAdmin);
        Assert.Equal(2, _db.Sessions.Count());               // клієнтський закрито, службовий відкрито
    }

    [Fact]
    public async Task Admin_cannot_interrupt_running_session()
    {
        await _sessions.ClientLoginAsync(_pc1, "ivan", "1111");
        Assert.False((await _sessions.ClientLoginAsync(_pc1, "boss", "2222")).Ok);
    }

    // --- збій сервера ---

    [Fact]
    public async Task After_server_crash_sessions_are_closed_at_last_heartbeat()
    {
        await _sessions.ClientLoginAsync(_pc1, "ivan", "1111");
        Age(_pc1, TimeSpan.FromMinutes(15) - Margin);
        var pc = _db.Pcs.Find(_pc1)!;
        pc.LastSeen = DateTime.UtcNow - TimeSpan.FromMinutes(5);   // останній сигнал 5 хв тому
        _db.SaveChanges();

        await _sessions.CloseAllAsync("Сервер було вимкнено", atLastSeen: true);

        var s = _db.Sessions.Single();
        Assert.NotNull(s.ClosedAt);
        Assert.Equal(10, s.ChargedMinutes);                        // 5 хв простою не списано
    }
}
