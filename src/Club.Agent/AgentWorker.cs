using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Club.Shared;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace Club.Agent;

/// <summary>
/// Основна логіка агента:
///  - підключається до сервера і перепідключається, якщо зв'язок зник;
///  - кожні 15 с надсилає heartbeat;
///  - виконує команди сервера.
/// Команди Lock/Unlock/ShowMessage передаються оболонці (Club.Shell) через ShellPipeServer.
/// </summary>
public class AgentWorker(IOptions<AgentOptions> options, ShellPipeServer shell, OwnerCache owners, ILogger<AgentWorker> log) : BackgroundService
{
    private HubConnection? _connection;

    /// <summary>Сеанс почався (true) або закінчився (false).</summary>
    public event Action<bool>? SessionActiveChanged;

    /// <summary>Клієнтський або гостьовий сеанс щойно завершився (ПК заблоковано).</summary>
    public event Action? SessionEnded;

    /// <summary>Сервер надіслав нову політику доступу до програм.</summary>
    public event Action<AppPolicy>? PolicyReceived;

    public bool IsSessionActive => !_locked;

    /// <summary>Вид поточного сеансу (None — ПК заблоковано або розблоковано вручну без сеансу).</summary>
    public SessionKind CurrentKind => _locked ? SessionKind.None : _kind;

    /// <summary>Чи заблоковано зараз робоче місце.</summary>
    public bool IsLocked => _locked;

    // Віддалене керування програмами: обробники задає AppControl
    public Func<List<RunningApp>>? ListAppsHandler { get; set; }
    public Func<string, AppActionResult>? CloseAppHandler { get; set; }
    public Func<string, AppActionResult>? LaunchAppHandler { get; set; }

    // Вид поточного сеансу (службовий — без таймера і без контролю програм)
    private SessionKind _kind = SessionKind.None;

    // Fail-closed: якщо зв'язку з сервером немає довше за цей час — ПК блокується
    private static readonly TimeSpan OfflineLockDelay = TimeSpan.FromSeconds(30);
    private DateTime? _disconnectedSince;
    private DateTime? _reconnectingSince;

    // Для вимірювання навантаження агента на процесор
    private TimeSpan _lastCpuTime = Process.GetCurrentProcess().TotalProcessorTime;
    private DateTime _lastCpuCheck = DateTime.UtcNow;

    // Офлайн-вхід власника: сервер недоступний, власник увійшов за локальною копією хешу
    private bool _offlineOwner;
    // Події, які не вдалося надіслати без зв'язку — надсилаються після відновлення
    private readonly ConcurrentQueue<string> _pendingReports = new();

    // Поточний стан робочого місця
    private bool _locked = true;
    private DateTime? _sessionEndsAt;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var o = options.Value;
        log.LogInformation("Агент запущено: версія {Version}, PID {Pid}, сервер {Url}",
            AgentVersion, Environment.ProcessId, o.ServerUrl);
        var hubUrl = o.ServerUrl.TrimEnd('/') + "/hubs/agent";

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, h =>
            {
                h.Headers["X-Agent-Key"] = o.AgentKey;
                // HTTPS: перевіряємо сертифікат сервера за відбитком (certificate pinning)
                h.HttpMessageHandlerFactory = handler =>
                {
                    if (handler is HttpClientHandler http)
                        http.ServerCertificateCustomValidationCallback = (_, cert, _, _) => IsTrustedServer(cert);
                    return handler;
                };
                h.WebSocketConfiguration = ws =>
                    ws.RemoteCertificateValidationCallback = (_, cert, _, _) => IsTrustedServer(cert);
            })
            .WithAutomaticReconnect(new ForeverRetryPolicy())
            .Build();

        _connection.On<AgentCommand>(HubMethods.Command, HandleCommandAsync);
        _connection.On<AppPolicy>(HubMethods.AppPolicy, policy =>
        {
            log.LogInformation("Отримано політику програм: {Count} дозволених, режим {Mode}",
                policy.Apps.Count, policy.Enforce ? "закривати" : "спостереження");
            shell.SetApps(policy.Apps);
            PolicyReceived?.Invoke(policy);
        });

        _connection.On<List<OwnerCredential>>(HubMethods.OwnerCredentials, owners.Save);

        // Відповідь адміністратора в чаті — показуємо в оболонці користувача
        _connection.On<ChatLine>(HubMethods.ChatFromAdmin, line => shell.AddChat(line));

        // Запити сервера з відповіддю: список, закриття й запуск програм у сеансі користувача
        _connection.On<List<RunningApp>>(HubMethods.ListApps, () =>
            Task.Run(() => ListAppsHandler?.Invoke() ?? new List<RunningApp>()));
        _connection.On<string, AppActionResult>(HubMethods.CloseApp, path => Task.Run(() =>
        {
            var result = CloseAppHandler?.Invoke(path) ?? new AppActionResult(false, "Не підтримується");
            log.LogInformation("Закриття програми {Path} на запит адміністратора: {Message}", path, result.Message);
            return result;
        }));
        _connection.On<string, AppActionResult>(HubMethods.LaunchApp, path => Task.Run(() =>
        {
            var result = LaunchAppHandler?.Invoke(path) ?? new AppActionResult(false, "Не підтримується");
            log.LogInformation("Запуск програми {Path} на запит адміністратора: {Message}", path, result.Message);
            return result;
        }));

        _connection.Reconnecting += error =>
        {
            _reconnectingSince = DateTime.UtcNow;
            log.LogWarning("Зв'язок із сервером втрачено, перепідключення…");
            return Task.CompletedTask;
        };
        _connection.Reconnected += async _ =>
        {
            var seconds = _reconnectingSince != null ? (DateTime.UtcNow - _reconnectingSince.Value).TotalSeconds : 0;
            _reconnectingSince = null;
            log.LogInformation("Зв'язок відновлено через {Seconds:0.0} с", seconds);
            await RegisterAsync(ct);
            await ReportAsync($"Зв'язок із сервером відновлено через {seconds:0.0} с");
        };

        try
        {
            // Перше підключення: пробуємо, доки сервер не стане доступним
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _connection.StartAsync(ct);
                    await RegisterAsync(ct);
                    break;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    log.LogWarning("Сервер {Url} недоступний ({Message}). Повтор через 5 с", hubUrl, ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
            }

            // Кожні 5 с перевіряємо зв'язок, кожні 15 с надсилаємо heartbeat
            var tick = 0;
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);

                if (_connection.State != HubConnectionState.Connected)
                {
                    _disconnectedSince ??= DateTime.UtcNow;
                    if (!_locked && !_offlineOwner && DateTime.UtcNow - _disconnectedSince > OfflineLockDelay)
                        LockLocally("Немає зв'язку з сервером. Зверніться до адміністратора.");
                    continue;
                }
                _disconnectedSince = null;

                if (++tick % 3 != 0) continue;
                var (cpu, memory) = MeasureSelf();
                try { await _connection.InvokeAsync(HubMethods.Heartbeat, cpu, memory, ct); }
                catch (Exception ex) { log.LogWarning("Heartbeat не вдався: {Message}", ex.Message); }
            }
        }
        catch (OperationCanceledException)
        {
            // служба зупиняється — це нормально
        }
    }

    /// <summary>Вхід клієнта з екрана блокування (запит приходить від оболонки).</summary>
    public async Task<LoginResult> ClientLoginAsync(string? login, string? password)
    {
        if (_connection?.State != HubConnectionState.Connected)
            return OfflineOwnerLogin(login, password);
        try
        {
            return await _connection.InvokeAsync<LoginResult>(HubMethods.ClientLogin, login, password);
        }
        catch (Exception ex)
        {
            log.LogWarning("Помилка входу клієнта: {Message}", ex.Message);
            return new LoginResult(false, "Помилка зв'язку з сервером");
        }
    }

    /// <summary>
    /// Вхід без сервера: дозволено лише власникам, за локальною копією хешу пароля.
    /// Відкривається службовий сеанс; після відновлення зв'язку сервер повертає ПК у звичайний стан.
    /// </summary>
    private LoginResult OfflineOwnerLogin(string? login, string? password)
    {
        var (ok, message) = owners.Verify(login, password);
        if (!ok) return new LoginResult(false, message);

        log.LogWarning("Офлайн-вхід власника «{Login}»", message);
        _offlineOwner = true;
        Execute(new AgentCommand(CommandType.Unlock, Kind: SessionKind.Service));
        _pendingReports.Enqueue($"Офлайн-вхід власника «{message}» на ПК, коли сервер був недоступний ({DateTime.Now:dd.MM HH:mm})");
        return new LoginResult(true);
    }

    /// <summary>Чи це наш сервер: SHA-256 відбиток сертифіката має збігатися з налаштуванням.</summary>
    private bool IsTrustedServer(X509Certificate? certificate)
    {
        var expected = (options.Value.ServerCertThumbprint ?? "").Replace(":", "").Replace(" ", "").ToUpperInvariant();
        if (certificate == null) return false;
        if (expected.Length == 0)
        {
            log.LogError("Не задано ServerCertThumbprint в appsettings.json — з'єднання з сервером відхилено");
            return false;
        }
        var actual = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        if (actual == expected) return true;
        log.LogError("Сертифікат сервера не збігається з очікуваним (отримано {Actual}) — можлива підміна сервера", actual);
        return false;
    }

    /// <summary>Клієнт сам завершує сеанс або сеанс завершується через неактивність (reason = "idle").</summary>
    /// <summary>Користувач пише адміністратору з оболонки (чат). Лише під час сеансу.</summary>
    public async Task<LoginResult> ClientChatAsync(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return new LoginResult(false, "Порожнє повідомлення");
        if (text.Length > ChatLine.MaxLength) return new LoginResult(false, $"Повідомлення задовге (до {ChatLine.MaxLength} символів)");
        if (_locked) return new LoginResult(false, "Чат доступний лише під час сеансу");
        if (_connection?.State != HubConnectionState.Connected)
            return new LoginResult(false, "Немає зв'язку з сервером. Зверніться до адміністратора особисто");
        try
        {
            var result = await _connection.InvokeAsync<LoginResult>(HubMethods.ChatFromUser, text);
            if (result.Ok) shell.AddChat(new ChatLine(false, "Ви", text, DateTime.UtcNow));
            return result;
        }
        catch (Exception ex)
        {
            log.LogWarning("Помилка надсилання повідомлення: {Message}", ex.Message);
            return new LoginResult(false, "Помилка зв'язку з сервером");
        }
    }

    public async Task<LoginResult> ClientLogoutAsync(string? reason = null)
    {
        // Без зв'язку з сервером ПК усе одно має звільнитися: блокуємо його локально
        if (_connection?.State != HubConnectionState.Connected)
        {
            LockLocally(reason == "idle" ? "Сеанс завершено через неактивність" : "Сеанс завершено");
            return new LoginResult(true);
        }
        try
        {
            return await _connection.InvokeAsync<LoginResult>(HubMethods.ClientLogout, reason);
        }
        catch (Exception ex)
        {
            log.LogWarning("Помилка завершення сеансу: {Message}", ex.Message);
            return new LoginResult(false, "Помилка зв'язку з сервером");
        }
    }

    /// <summary>
    /// Навантаження самого агента: частка процесорного часу (% від усіх ядер)
    /// з моменту попереднього виміру і обсяг робочої пам'яті в МБ.
    /// </summary>
    private (double CpuPercent, double MemoryMb) MeasureSelf()
    {
        using var me = Process.GetCurrentProcess();
        var now = DateTime.UtcNow;
        var cpuTime = me.TotalProcessorTime;
        var wall = (now - _lastCpuCheck).TotalMilliseconds;
        var cpu = wall > 0 ? (cpuTime - _lastCpuTime).TotalMilliseconds / (wall * Environment.ProcessorCount) * 100 : 0;
        _lastCpuTime = cpuTime;
        _lastCpuCheck = now;
        return (Math.Round(cpu, 2), Math.Round(me.WorkingSet64 / 1024.0 / 1024.0, 1));
    }

    /// <summary>Заблокувати ПК без команди сервера (немає зв'язку).</summary>
    private void LockLocally(string reason)
    {
        log.LogWarning("Локальне блокування: {Reason}", reason);
        Execute(new AgentCommand(CommandType.Lock, reason));
    }

    /// <summary>Повідомити сервер про подію на ПК (записується в журнал панелі).</summary>
    public async Task ReportAsync(string text)
    {
        if (_connection?.State != HubConnectionState.Connected)
        {
            if (_pendingReports.Count < 100) _pendingReports.Enqueue(text);
            return;
        }
        try { await _connection.SendAsync(HubMethods.Report, text); }
        catch (Exception ex) { log.LogWarning("Не вдалося надіслати подію: {Message}", ex.Message); }
    }

    /// <summary>Версія агента разом із датою збірки, напр. "1.1.0+2026.11.15-19.20".</summary>
    private static readonly string AgentVersion =
        typeof(AgentWorker).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion
        ?? typeof(AgentWorker).Assembly.GetName().Version?.ToString() ?? "?";

    private async Task RegisterAsync(CancellationToken ct)
    {
        var version = AgentVersion;
        var pcId = await _connection!.InvokeAsync<int>(HubMethods.Register,
            new AgentHello(Environment.MachineName, version), ct);
        log.LogInformation("Зареєстровано на сервері, Id ПК = {Id}", pcId);

        // Надсилаємо події, що накопичилися, поки не було зв'язку
        for (var n = _pendingReports.Count; n > 0 && _pendingReports.TryDequeue(out var text); n--)
            await ReportAsync(text);
    }

    /// <summary>Виконує команду і повідомляє серверу результат.</summary>
    private async Task HandleCommandAsync(AgentCommand cmd)
    {
        log.LogInformation("Отримано команду {Type} {Text}", cmd.Type, cmd.Text);

        string? error = null;
        try { Execute(cmd); }
        catch (Exception ex)
        {
            error = ex.Message;
            log.LogError(ex, "Не вдалося виконати {Type}", cmd.Type);
        }

        // SendAsync, а не InvokeAsync: не чекаємо відповіді сервера всередині обробника
        try { await _connection!.SendAsync(HubMethods.CommandResult, cmd.Type, error == null, error); }
        catch (Exception ex) { log.LogWarning("Не вдалося надіслати результат: {Message}", ex.Message); }
    }

    private void Execute(AgentCommand cmd)
    {
        switch (cmd.Type)
        {
            case CommandType.Lock:
                // Чи завершився щойно звичайний (не службовий) сеанс — тоді прибираємо за користувачем
                var userSessionEnded = !_locked && _kind != SessionKind.Service;
                _locked = true;
                _offlineOwner = false;
                _kind = SessionKind.None;
                _sessionEndsAt = null;
                shell.SetState(true, null, cmd.Text, SessionKind.None);
                SessionActiveChanged?.Invoke(false);
                if (userSessionEnded) SessionEnded?.Invoke();
                log.LogInformation(">>> РОБОЧЕ МІСЦЕ ЗАБЛОКОВАНО");
                break;

            case CommandType.Unlock:
                _locked = false;
                _kind = cmd.Kind;
                _sessionEndsAt = cmd.SessionEndsAt;
                shell.SetState(false, cmd.SessionEndsAt, null, _kind);
                // Контроль програм не діє у службовому сеансі адміністратора
                SessionActiveChanged?.Invoke(_kind != SessionKind.Service);
                log.LogInformation(">>> РОЗБЛОКОВАНО, сеанс до {End}", cmd.SessionEndsAt?.ToLocalTime());
                break;

            case CommandType.SessionUpdate:
                _sessionEndsAt = cmd.SessionEndsAt;
                shell.SetState(_locked, cmd.SessionEndsAt, null, _kind);
                log.LogInformation(">>> Сеанс продовжено до {End}", cmd.SessionEndsAt?.ToLocalTime());
                break;

            case CommandType.ShowMessage:
                shell.ShowMessage(cmd.Text ?? "");
                log.LogInformation(">>> ПОВІДОМЛЕННЯ: {Text}", cmd.Text);
                break;

            case CommandType.Reboot:
                RunShutdown("/r /f /t 5");
                break;

            case CommandType.Shutdown:
                RunShutdown("/s /f /t 5");
                break;
        }

        log.LogInformation("Стан ПК: {State}", _locked ? "заблоковано" : "розблоковано");
    }

    private void RunShutdown(string args)
    {
        // Повний шлях: служба не завжди має той самий PATH, що й користувач
        var exe = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
        log.LogInformation("Виконую {Exe} {Args}", exe, args);

        using var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Не вдалося запустити shutdown.exe");

        var stderr = p.StandardError.ReadToEnd();
        if (p.WaitForExit(5000) && p.ExitCode != 0)
            throw new InvalidOperationException($"shutdown.exe завершився з кодом {p.ExitCode}: {stderr.Trim()}");
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        log.LogInformation("Агент зупиняється (команда від Windows)");
        if (_connection != null) await _connection.DisposeAsync();
        await base.StopAsync(ct);
    }
}

/// <summary>Перепідключатися безкінечно: 2, 4, 6 … до 30 секунд між спробами.</summary>
public class ForeverRetryPolicy : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext ctx) =>
        TimeSpan.FromSeconds(Math.Min(30, 2 + ctx.PreviousRetryCount * 2));
}
