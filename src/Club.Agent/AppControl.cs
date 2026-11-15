using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Club.Shared;
using Microsoft.Extensions.Options;

namespace Club.Agent;

/// <summary>
/// Контроль доступу до програм (білий список).
/// Під час сеансу кожні 3 секунди переглядає процеси в сеансах користувачів і перевіряє,
/// чи дозволено програму:
///  - системні програми Windows (папка C:\Windows) — завжди дозволені;
///  - папки зі списку AlwaysAllowedFolders в appsettings.json (драйвери, антивірус тощо);
///  - програми з каталогу, який задає адміністратор, і все з їхніх папок
///    (ігри часто запускають допоміжні .exe зі своєї папки).
/// Режим "спостереження" лише повідомляє про порушення, режим "закривати" — завершує процес.
/// </summary>
public class AppControl : BackgroundService
{
    private readonly AgentWorker _agent;
    private readonly ILogger<AppControl> _log;
    private readonly string[] _alwaysAllowed;
    private readonly object _lock = new();

    private AppPolicy _policy = new(false, []);
    private bool _sessionActive;
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);

    public AppControl(AgentWorker agent, IOptions<AgentOptions> options, ILogger<AppControl> log)
    {
        _agent = agent;
        _log = log;

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        _alwaysAllowed = new[] { windows, AppContext.BaseDirectory }
            .Concat(options.Value.AlwaysAllowedFolders)
            .Select(NormalizeFolder)
            .ToArray();

        agent.PolicyReceived += p => { lock (_lock) _policy = p; };
        agent.SessionActiveChanged += active =>
        {
            lock (_lock)
            {
                _sessionActive = active;
                _reported.Clear(); // новий сеанс — порушення рахуємо заново
            }
        };
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { await ScanAsync(); }
                catch (Exception ex) { _log.LogWarning("Помилка перевірки процесів: {Message}", ex.Message); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ScanAsync()
    {
        AppPolicy policy;
        lock (_lock)
        {
            if (!_sessionActive) return;
            policy = _policy;
        }

        var allowedFiles = new HashSet<string>(policy.Apps.Select(a => a.Path), StringComparer.OrdinalIgnoreCase);
        var allowedFolders = policy.Apps
            .Select(a => Path.GetDirectoryName(a.Path))
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => NormalizeFolder(d!))
            .Concat(_alwaysAllowed)
            .ToArray();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.SessionId == 0) continue;               // системні служби — не чіпаємо
                var path = GetPath(process.Id);
                if (path == null) continue;                          // захищені процеси Windows

                if (allowedFiles.Contains(path)) continue;
                if (allowedFolders.Any(f => path.StartsWith(f, StringComparison.OrdinalIgnoreCase))) continue;

                var name = Path.GetFileName(path);
                if (policy.Enforce)
                {
                    try
                    {
                        process.Kill();
                        _log.LogInformation("Закрито недозволену програму {Path}", path);
                        await ReportOnceAsync("closed:" + path, $"Закрито недозволену програму {name} ({path})");
                    }
                    catch (Exception ex)
                    {
                        await ReportOnceAsync("failed:" + path, $"Не вдалося закрити недозволену програму {name}: {ex.Message}");
                    }
                }
                else
                {
                    await ReportOnceAsync("seen:" + path, $"Запущено програму поза каталогом: {name} ({path})");
                }
            }
        }
    }

    /// <summary>
    /// Про кожну подію (помічено / закрито / не вдалося закрити) для кожної програми
    /// повідомляємо один раз за сеанс, щоб не засмічувати журнал.
    /// </summary>
    private async Task ReportOnceAsync(string key, string text)
    {
        lock (_lock)
        {
            if (!_reported.Add(key)) return;
        }
        await _agent.ReportAsync(text);
    }

    private static string NormalizeFolder(string folder) =>
        Path.GetFullPath(folder).TrimEnd('\\') + "\\";

    // --- Шлях до .exe процесу через Windows API (працює і для процесів інших користувачів) ---

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static string? GetPath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
