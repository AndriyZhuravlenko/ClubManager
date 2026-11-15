using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Club.Shared;
using Microsoft.AspNetCore.SignalR.Client;

// ============================================================================
// Навантажувальний тест Club Manager.
//
// Запускає N віртуальних агентів (кожен — окреме SignalR-з'єднання, як справжній ПК),
// які реєструються на сервері й регулярно надсилають heartbeat. Паралельно програма
// імітує адміністратора: оновлює список ПК і надсилає команди на віртуальні ПК.
//
// Що вимірюється:
//   - час підключення й реєстрації агента;
//   - час виклику heartbeat (запис у базу + відповідь);
//   - час доставки команди: від запиту адміністратора до отримання агентом;
//   - час відповіді API панелі (/api/pcs);
//   - процесор і пам'ять процесу сервера (якщо тест запущено на тому ж комп'ютері).
//
// Після тесту віртуальні ПК видаляються з панелі (потрібен вхід власника).
// ============================================================================

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = Encoding.UTF8;

var opt = Options.Parse(args);
if (opt == null) return 1;

Console.WriteLine($"""
    === Навантажувальний тест Club Manager ===
    Сервер:        {opt.HubUrl}
    Агентів:       {opt.Agents}
    Тривалість:    {opt.Duration} с (+ підключення)
    Heartbeat:     кожні {opt.Interval} с (справжній агент — 15 с)
    Команди:       {(opt.Login != null ? $"кожні {opt.CommandEvery} с, від імені «{opt.Login}»" : "вимкнено (не задано --login)")}

    """);

if (opt.Thumbprint == null)
{
    Console.WriteLine("Не знайдено відбиток сертифіката сервера (server-cert.thumbprint.txt). Вкажіть --thumbprint або --thumbprint-file.");
    return 1;
}

// ---------- Вхід адміністратора в API панелі ----------
HttpClient? api = null;
if (opt.Login != null)
{
    api = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(opt.ApiUrl) };
    var password = opt.Password ?? ReadPassword($"Пароль адміністратора «{opt.Login}»: ");
    var r = await api.PostAsJsonAsync("/api/auth/login", new { login = opt.Login, password });
    if (!r.IsSuccessStatusCode)
    {
        Console.WriteLine($"Не вдалося увійти в панель: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return 1;
    }
    Console.WriteLine("Вхід у панель виконано.");
}

var stats = new Stats();
var agents = new List<FakeAgent>();
var runId = DateTime.Now.ToString("HHmmss");

// ---------- 1. Підключення агентів (по 20 одночасно, як після масового ввімкнення ПК) ----------
Console.WriteLine($"Підключення {opt.Agents} агентів…");
var connectWatch = Stopwatch.StartNew();
using (var gate = new SemaphoreSlim(20))
{
    var tasks = Enumerable.Range(1, opt.Agents).Select(async i =>
    {
        await gate.WaitAsync();
        try
        {
            var agent = new FakeAgent($"{opt.Prefix}-{runId}-{i:000}", opt, stats);
            if (await agent.ConnectAsync()) lock (agents) agents.Add(agent);
        }
        finally { gate.Release(); }
    });
    await Task.WhenAll(tasks);
}
Console.WriteLine($"Підключено {agents.Count} з {opt.Agents} за {connectWatch.Elapsed.TotalSeconds:0.0} с");
if (agents.Count == 0) { Console.WriteLine("Жоден агент не підключився — перевірте адресу, ключ і відбиток."); return 1; }

// ---------- 2. Навантаження ----------
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(opt.Duration));
var monitor = new ServerMonitor(opt.ServerProcess);
var work = new List<Task> { monitor.RunAsync(cts.Token) };
work.AddRange(agents.Select(a => a.HeartbeatLoopAsync(cts.Token)));
if (api != null)
{
    work.Add(PanelLoopAsync(api, stats, cts.Token));
    work.Add(CommandLoopAsync(api, agents, opt, stats, cts.Token));
}
work.Add(ProgressAsync(cts.Token));

Console.WriteLine($"Навантаження {opt.Duration} с…");
try { await Task.WhenAll(work); } catch (OperationCanceledException) { }
await Task.Delay(1500);   // дочекатися команд, що ще в дорозі

// ---------- 3. Результати ----------
var report = stats.Report(agents.Count, opt, monitor);
Console.WriteLine();
Console.WriteLine(report.Text);
report.AppendCsv(opt.CsvPath);
Console.WriteLine($"Рядок результатів додано у файл {Path.GetFullPath(opt.CsvPath)}");

// ---------- 4. Прибирання ----------
Console.WriteLine();
Console.WriteLine("Відключення агентів…");
await Task.WhenAll(agents.Select(a => a.DisposeAsync().AsTask()));
if (api != null)
{
    await Task.Delay(1000);   // сервер має побачити, що ПК вийшли з мережі
    var pcs = await api.GetFromJsonAsync<List<PcDto>>("/api/pcs") ?? [];
    var mine = pcs.Where(p => p.MachineName.StartsWith($"{opt.Prefix}-{runId}-")).ToList();
    var deleted = 0;
    // Кілька спроб: сервер може ще обробляти відключення окремих агентів
    for (var attempt = 0; attempt < 3 && mine.Count > 0; attempt++)
    {
        if (attempt > 0) await Task.Delay(2000);
        var left = new List<PcDto>();
        foreach (var pc in mine)
            if ((await api.DeleteAsync($"/api/pcs/{pc.Id}")).IsSuccessStatusCode) deleted++; else left.Add(pc);
        mine = left;
    }
    Console.WriteLine($"Віртуальні ПК видалено з панелі: {deleted} з {deleted + mine.Count}" +
        (mine.Count > 0 ? $" (не вдалося: {string.Join(", ", mine.Select(p => p.MachineName))} — видаліть у панелі вручну)" : ""));
}
else
{
    Console.WriteLine($"Віртуальні ПК ({opt.Prefix}-{runId}-…) залишилися в панелі. Щоб видаляти їх автоматично, запускайте тест з --login власника.");
}
return 0;

// ============================================================================

// Імітація панелі: адміністратор тримає відкритий список ПК
static async Task PanelLoopAsync(HttpClient api, Stats stats, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var r = await api.GetAsync("/api/pcs", ct);
            await r.Content.ReadAsByteArrayAsync(ct);
            if (r.IsSuccessStatusCode) stats.Api.Add(sw.Elapsed.TotalMilliseconds); else stats.Error("api " + (int)r.StatusCode);
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex) { stats.Error("api: " + ex.Message); }
        try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
    }
}

// Імітація адміністратора: надсилає повідомлення на випадковий віртуальний ПК
static async Task CommandLoopAsync(HttpClient api, List<FakeAgent> agents, Options opt, Stats stats, CancellationToken ct)
{
    var pcs = await api.GetFromJsonAsync<List<PcDto>>("/api/pcs", ct) ?? [];
    var ids = agents.ToDictionary(a => a.MachineName, a => a);
    var targets = pcs.Where(p => ids.ContainsKey(p.MachineName)).Select(p => (p.Id, Agent: ids[p.MachineName])).ToList();
    if (targets.Count == 0) return;
    var seq = 0;
    while (!ct.IsCancellationRequested)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(opt.CommandEvery), ct); } catch (OperationCanceledException) { break; }
        var (id, agent) = targets[Random.Shared.Next(targets.Count)];
        var marker = $"LT#{++seq}";
        agent.Expect(marker);
        try
        {
            var r = await api.PostAsJsonAsync($"/api/pcs/{id}/command", new { type = "ShowMessage", text = marker }, ct);
            if (!r.IsSuccessStatusCode) stats.Error("command " + (int)r.StatusCode);
            else stats.CommandsSent++;
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex) { stats.Error("command: " + ex.Message); }
    }
}

async Task ProgressAsync(CancellationToken ct)
{
    var start = DateTime.UtcNow;
    while (!ct.IsCancellationRequested)
    {
        try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
        Console.WriteLine($"  {(DateTime.UtcNow - start).TotalSeconds,4:0} с: heartbeat {stats.Heartbeat.Count}, " +
                          $"команд доставлено {stats.Command.Count}, помилок {stats.Errors}" +
                          (monitor.Last is { } m ? $", сервер: {m.Cpu:0}% CPU, {m.MemoryMb:0} МБ" : ""));
    }
}

static string ReadPassword(string prompt)
{
    Console.Write(prompt);
    var sb = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
        sb.Append(key.KeyChar);
    }
    Console.WriteLine();
    return sb.ToString();
}

// ============================================================================

record PcDto(int Id, string MachineName);

/// <summary>Віртуальний агент: те саме з'єднання і ті самі виклики, що й справжній агент на ПК.</summary>
sealed class FakeAgent(string machineName, Options opt, Stats stats) : IAsyncDisposable
{
    public string MachineName { get; } = machineName;
    private HubConnection? _connection;
    private readonly ConcurrentDictionary<string, long> _expected = new();

    public void Expect(string marker) => _expected[marker] = Stopwatch.GetTimestamp();

    public async Task<bool> ConnectAsync()
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(opt.HubUrl, h =>
            {
                h.Headers["X-Agent-Key"] = opt.AgentKey!;
                h.HttpMessageHandlerFactory = handler =>
                {
                    if (handler is HttpClientHandler http)
                        http.ServerCertificateCustomValidationCallback = (_, cert, _, _) => opt.IsTrusted(cert);
                    return handler;
                };
                h.WebSocketConfiguration = ws =>
                    ws.RemoteCertificateValidationCallback = (_, cert, _, _) => opt.IsTrusted(cert);
            })
            .Build();

        // Сервер надсилає команди — агент підтверджує їх, як справжній
        _connection.On<AgentCommand>(HubMethods.Command, async cmd =>
        {
            if (cmd.Type == CommandType.ShowMessage && cmd.Text != null && _expected.TryRemove(cmd.Text, out var sent))
                stats.Command.Add(Stopwatch.GetElapsedTime(sent).TotalMilliseconds);
            try { await _connection.InvokeAsync(HubMethods.CommandResult, cmd.Type, true, (string?)null); }
            catch (Exception ex) { stats.Error("result: " + ex.Message); }
        });
        _connection.On<JsonElement>(HubMethods.AppPolicy, _ => { });
        _connection.On<JsonElement>(HubMethods.OwnerCredentials, _ => { });

        var sw = Stopwatch.StartNew();
        try
        {
            await _connection.StartAsync();
            await _connection.InvokeAsync<int>(HubMethods.Register, new AgentHello(MachineName, "loadtest"));
            stats.Connect.Add(sw.Elapsed.TotalMilliseconds);
            return true;
        }
        catch (Exception ex)
        {
            stats.Error("connect: " + ex.GetBaseException().Message);
            return false;
        }
    }

    public async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        // Випадковий зсув, щоб агенти не надсилали heartbeat в одну мить
        try { await Task.Delay(Random.Shared.Next(opt.Interval * 1000), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested && _connection != null)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await _connection.InvokeAsync(HubMethods.Heartbeat, 0.1, 60.0, ct);
                stats.Heartbeat.Add(sw.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { stats.Error("heartbeat: " + ex.Message); }
            try { await Task.Delay(opt.Interval * 1000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null) await _connection.DisposeAsync();
    }
}

/// <summary>Збирає виміри і рахує перцентилі.</summary>
sealed class Stats
{
    public Series Connect { get; } = new();
    public Series Heartbeat { get; } = new();
    public Series Command { get; } = new();
    public Series Api { get; } = new();
    public int CommandsSent;
    private int _errors;
    private readonly ConcurrentDictionary<string, int> _errorKinds = new();

    public int Errors => _errors;

    public void Error(string text)
    {
        Interlocked.Increment(ref _errors);
        _errorKinds.AddOrUpdate(text.Length > 80 ? text[..80] : text, 1, (_, n) => n + 1);
    }

    public Report Report(int agents, Options opt, ServerMonitor monitor)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== Результати: {agents} агентів, {opt.Duration} с, heartbeat кожні {opt.Interval} с ===");
        sb.AppendLine($"{"Операція",-34} {"к-сть",7} {"сер.",8} {"p50",8} {"p95",8} {"p99",8} {"макс",8}   (мс)");
        void Row(string name, Series s) =>
            sb.AppendLine(s.Count == 0 ? $"{name,-34} {"—",7}"
                : $"{name,-34} {s.Count,7} {s.Mean,8:0.0} {s.P(50),8:0.0} {s.P(95),8:0.0} {s.P(99),8:0.0} {s.Max,8:0.0}");
        Row("Підключення + реєстрація агента", Connect);
        Row("Heartbeat (запис у базу)", Heartbeat);
        Row("Доставка команди адмін → ПК", Command);
        Row("API панелі GET /api/pcs", Api);
        var throughput = Heartbeat.Count / (double)opt.Duration;
        sb.AppendLine($"Пропускна здатність: {throughput:0.0} heartbeat/с");
        if (CommandsSent > 0) sb.AppendLine($"Команд надіслано: {CommandsSent}, доставлено: {Command.Count}");
        sb.AppendLine(monitor.Samples.Count > 0
            ? $"Сервер ({opt.ServerProcess}): процесор сер. {monitor.Samples.Average(x => x.Cpu):0.0}%, макс. {monitor.Samples.Max(x => x.Cpu):0.0}%; " +
              $"пам'ять макс. {monitor.Samples.Max(x => x.MemoryMb):0} МБ (усі ядра = 100%)"
            : $"Процес сервера «{opt.ServerProcess}» на цьому комп'ютері не знайдено — процесор і пам'ять не виміряно");
        sb.AppendLine($"Помилок: {Errors}");
        foreach (var (kind, n) in _errorKinds.OrderByDescending(x => x.Value).Take(5)) sb.AppendLine($"  {n} × {kind}");
        return new Report(sb.ToString(), string.Join(",",
            DateTime.Now.ToString("yyyy-MM-dd HH:mm"), agents, opt.Duration, opt.Interval,
            F(Connect.P(50)), F(Connect.P(95)), F(Heartbeat.P(50)), F(Heartbeat.P(95)), F(Heartbeat.P(99)),
            F(Command.P(50)), F(Command.P(95)), F(Api.P(50)), F(Api.P(95)), F(throughput),
            monitor.Samples.Count > 0 ? F(monitor.Samples.Average(x => x.Cpu)) : "",
            monitor.Samples.Count > 0 ? F(monitor.Samples.Max(x => x.MemoryMb)) : "",
            Errors));
    }

    private static string F(double v) => double.IsNaN(v) ? "" : v.ToString("0.0", CultureInfo.InvariantCulture);
}

sealed class Series
{
    private readonly List<double> _values = [];
    public void Add(double v) { lock (_values) _values.Add(v); }
    public int Count { get { lock (_values) return _values.Count; } }
    public double Mean { get { lock (_values) return _values.Count == 0 ? double.NaN : _values.Average(); } }
    public double Max { get { lock (_values) return _values.Count == 0 ? double.NaN : _values.Max(); } }
    public double P(int percentile)
    {
        lock (_values)
        {
            if (_values.Count == 0) return double.NaN;
            var sorted = _values.Order().ToList();
            var index = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
            return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
        }
    }
}

sealed record Report(string Text, string CsvRow)
{
    private const string Header = "date,agents,duration_s,interval_s,connect_p50,connect_p95,heartbeat_p50,heartbeat_p95,heartbeat_p99," +
                                  "command_p50,command_p95,api_p50,api_p95,heartbeats_per_s,server_cpu_avg,server_mem_max_mb,errors";
    public void AppendCsv(string path)
    {
        var exists = File.Exists(path);
        using var w = new StreamWriter(path, append: true, Encoding.UTF8);
        if (!exists) w.WriteLine(Header);
        w.WriteLine(CsvRow);
    }
}

/// <summary>Щосекунди знімає процесор і пам'ять процесу сервера (якщо він на цьому ж комп'ютері).</summary>
sealed class ServerMonitor(string processName)
{
    public List<(double Cpu, double MemoryMb)> Samples { get; } = [];
    public (double Cpu, double MemoryMb)? Last => Samples.Count > 0 ? Samples[^1] : null;

    public async Task RunAsync(CancellationToken ct)
    {
        var process = Process.GetProcessesByName(processName).FirstOrDefault();
        if (process == null) return;
        var lastCpu = process.TotalProcessorTime;
        var lastAt = Stopwatch.GetTimestamp();
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
            try
            {
                process.Refresh();
                var cpu = process.TotalProcessorTime;
                var elapsed = Stopwatch.GetElapsedTime(lastAt).TotalMilliseconds;
                var percent = (cpu - lastCpu).TotalMilliseconds / elapsed / Environment.ProcessorCount * 100;
                lastCpu = cpu;
                lastAt = Stopwatch.GetTimestamp();
                lock (Samples) Samples.Add((percent, process.WorkingSet64 / 1024.0 / 1024));
            }
            catch { break; }   // сервер зупинився
        }
    }
}

/// <summary>Параметри командного рядка.</summary>
sealed class Options
{
    public string Server = "https://127.0.0.1:5001";
    public string ApiUrl = "http://127.0.0.1:5000";
    public string? AgentKey;
    public string? Thumbprint;
    public int Agents = 50;
    public int Duration = 60;
    public int Interval = 15;
    public double CommandEvery = 1;
    public string? Login;
    public string? Password;
    public string Prefix = "LOADTEST";
    public string ServerProcess = "Club.Server";
    public string CsvPath = "loadtest-results.csv";

    public string HubUrl => Server.TrimEnd('/') + "/hubs/agent";

    public bool IsTrusted(System.Security.Cryptography.X509Certificates.X509Certificate? cert) =>
        cert != null && Thumbprint != null &&
        cert.GetCertHashString(HashAlgorithmName.SHA256).Equals(Thumbprint, StringComparison.OrdinalIgnoreCase);

    public static Options? Parse(string[] args)
    {
        var o = new Options();
        string? thumbFile = null;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Після {args[i]} потрібне значення");
            switch (args[i])
            {
                case "--server": o.Server = Next(); break;
                case "--api": o.ApiUrl = Next(); break;
                case "--key": o.AgentKey = Next(); break;
                case "--thumbprint": o.Thumbprint = Next(); break;
                case "--thumbprint-file": thumbFile = Next(); break;
                case "--agents": o.Agents = int.Parse(Next()); break;
                case "--duration": o.Duration = int.Parse(Next()); break;
                case "--interval": o.Interval = Math.Max(1, int.Parse(Next())); break;
                case "--command-every": o.CommandEvery = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--login": o.Login = Next(); break;
                case "--password": o.Password = Next(); break;
                case "--prefix": o.Prefix = Next(); break;
                case "--process": o.ServerProcess = Next(); break;
                case "--csv": o.CsvPath = Next(); break;
                case "-h" or "--help":
                    Console.WriteLine("""
                        Параметри:
                          --agents N           кількість віртуальних агентів (50)
                          --duration S         тривалість навантаження, с (60)
                          --interval S         як часто кожен агент надсилає heartbeat, с (15, як справжній)
                          --login L            логін власника: вимірювати команди й API та прибрати ПК після тесту
                          --command-every S    як часто надсилати команду на випадковий ПК, с (1)
                          --server URL         адреса для агентів (https://127.0.0.1:5001)
                          --api URL            адреса панелі (http://127.0.0.1:5000)
                          --key K              ключ агентів з appsettings.json сервера
                          --thumbprint T       відбиток сертифіката (за замовчуванням з server-cert.thumbprint.txt)
                          --csv FILE           куди дописати рядок результатів (loadtest-results.csv)
                        """);
                    return null;
                default: Console.WriteLine($"Невідомий параметр {args[i]} (див. --help)"); return null;
            }
        }

        // Відбиток сертифіката: файл, який сервер створює поруч із собою
        if (o.Thumbprint == null)
        {
            var candidates = thumbFile != null ? new[] { thumbFile } : new[]
            {
                "server-cert.thumbprint.txt",
                Path.Combine("src", "Club.Server", "server-cert.thumbprint.txt"),
                Path.Combine("..", "..", "..", "..", "..", "src", "Club.Server", "server-cert.thumbprint.txt"),
                Path.Combine("publish", "ClubServer", "server-cert.thumbprint.txt"),
            };
            var found = candidates.FirstOrDefault(File.Exists);
            if (found != null) o.Thumbprint = File.ReadAllText(found).Trim().Replace(":", "").Replace(" ", "");
        }
        // Ключ агентів: з налаштувань сервера, якщо не задано явно
        if (o.AgentKey == null)
        {
            foreach (var file in new[] { "appsettings.json", Path.Combine("src", "Club.Server", "appsettings.json") })
            {
                if (!File.Exists(file)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    if (doc.RootElement.TryGetProperty("AgentKey", out var k)) { o.AgentKey = k.GetString(); break; }
                }
                catch { }
            }
            o.AgentKey ??= "change-me-club-2026";
        }
        return o;
    }
}
