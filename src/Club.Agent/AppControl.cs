using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Club.Shared;
using Microsoft.Extensions.Options;

namespace Club.Agent;

/// <summary>
/// Контроль доступу до програм.
///
/// Чорний список діє ЗАВЖДИ (і під час сеансу, і на екрані блокування), крім службового
/// сеансу адміністратора: заборонені програми закриваються в будь-якому режимі.
/// Власні файли агента і критичні процеси Windows не закриваються ніколи.
///
/// Білий список діє під час сеансу. Кожні 2 секунди агент переглядає процеси в сеансах
/// користувачів і перевіряє, чи дозволено програму:
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
    private readonly bool _closeAppsOnSessionEnd;
    private readonly string[] _machineCleanup;
    private readonly object _lock = new();

    private AppPolicy _policy = new(false, []);
    private bool _sessionActive;
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);

    // Процеси, без яких Windows не працює, — їх не закриваємо, навіть якщо їх внесли до чорного списку
    private static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "winlogon.exe", "logonui.exe", "csrss.exe", "dwm.exe", "lsass.exe", "services.exe",
        "smss.exe", "wininit.exe", "svchost.exe", "fontdrvhost.exe", "sihost.exe", "ctfmon.exe", "conhost.exe",
        "userinit.exe", "consent.exe", "runtimebroker.exe", "startmenuexperiencehost.exe", "shellexperiencehost.exe",
        "searchhost.exe", "textinputhost.exe", "lockapp.exe", "taskhostw.exe", "dllhost.exe", "audiodg.exe"
    };

    public AppControl(AgentWorker agent, IOptions<AgentOptions> options, ILogger<AppControl> log)
    {
        _agent = agent;
        _log = log;

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        _alwaysAllowed = new[] { windows, AppContext.BaseDirectory }
            .Concat(options.Value.AlwaysAllowedFolders)
            .Select(NormalizeFolder)
            .ToArray();

        _closeAppsOnSessionEnd = options.Value.CloseAppsOnSessionEnd;
        _machineCleanup = options.Value.MachineCleanup;
        agent.SessionEnded += () => _ = Task.Run(CloseUserAppsAsync);

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
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
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
        bool whitelistActive;
        lock (_lock)
        {
            policy = _policy;
            whitelistActive = _sessionActive;
        }

        // Службовий сеанс адміністратора — жодних обмежень
        if (_agent.CurrentKind == SessionKind.Service) return;

        var blocked = (policy.Blocked ?? []).Select(BlockRule.Create).ToList();
        if (!whitelistActive && blocked.Count == 0) return;

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

                var name = Path.GetFileName(path);
                var isOwnOrCritical = Critical.Contains(name) ||
                    path.StartsWith(_alwaysAllowed[1], StringComparison.OrdinalIgnoreCase); // папка агента

                // 1. Чорний список — закриваємо завжди
                if (!isOwnOrCritical && blocked.Any(rule => rule.Matches(path, name)))
                {
                    try
                    {
                        process.Kill();
                        await ReportOnceAsync("blocked:" + path, $"Закрито заборонену програму {name} ({path})");
                    }
                    catch (Exception ex)
                    {
                        await ReportOnceAsync("blockfail:" + path, $"Не вдалося закрити заборонену програму {name}: {ex.Message}");
                    }
                    continue;
                }

                // 2. Білий список — лише під час сеансу
                if (!whitelistActive) continue;

                if (IsAllowed(path, allowedFiles, allowedFolders)) continue;

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
    /// Після завершення сеансу закриває всі програми користувача (месенджери, браузери, ігри),
    /// щоб наступний користувач не потрапив у чужі акаунти. Не чіпає системні програми Windows,
    /// власні файли агента, завжди дозволені папки і критичні процеси.
    /// </summary>
    private async Task CloseUserAppsAsync()
    {
        if (!_closeAppsOnSessionEnd) return;
        var closed = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.SessionId == 0) continue;
                var path = GetPath(process.Id);
                if (path == null) continue;
                var name = Path.GetFileName(path);
                if (Critical.Contains(name)) continue;
                if (_alwaysAllowed.Any(f => path.StartsWith(f, StringComparison.OrdinalIgnoreCase))) continue;
                try
                {
                    process.Kill(entireProcessTree: true);
                    closed.Add(name);
                }
                catch { /* процес уже завершився */ }
            }
        }
        if (closed.Count > 0)
            await _agent.ReportAsync($"Після завершення сеансу закрито програм: {closed.Count} ({string.Join(", ", closed.Distinct().Take(10))})");

        // Дані входу, які програми зберігають поза профілем користувача (напр., Steam)
        await Task.Delay(TimeSpan.FromSeconds(1)); // щоб закриті програми відпустили файли
        var deleted = 0;
        foreach (var path in _machineCleanup) deleted += DeleteMachinePath(Environment.ExpandEnvironmentVariables(path));
        if (deleted > 0) await _agent.ReportAsync($"Після завершення сеансу видалено файлів із даними входу: {deleted}");
    }

    /// <summary>Видаляє файл, папку або файли за шаблоном в останній частині шляху. Повертає кількість.</summary>
    private int DeleteMachinePath(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            var pattern = Path.GetFileName(path);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(pattern) || !Directory.Exists(dir)) return 0;

            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                var count = 0;
                foreach (var file in Directory.GetFiles(dir, pattern))
                    try { File.Delete(file); count++; } catch { }
                return count;
            }
            if (File.Exists(path)) { File.Delete(path); return 1; }
            if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); return 1; }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Не вдалося видалити {Path}: {Message}", path, ex.Message);
        }
        return 0;
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

    /// <summary>Чи дозволено запуск: файл є в каталозі або лежить у дозволеній папці (папки — з "\" у кінці).</summary>
    internal static bool IsAllowed(string path, ISet<string> allowedFiles, IEnumerable<string> allowedFolders) =>
        allowedFiles.Contains(path) ||
        allowedFolders.Any(f => path.StartsWith(f, StringComparison.OrdinalIgnoreCase));

    internal static string NormalizeFolder(string folder) =>
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

/// <summary>
/// Правило чорного списку: або ім'я файлу (з підтримкою * і ?), або повний шлях до файлу чи папки.
/// </summary>
internal sealed class BlockRule
{
    private readonly string? _pathPrefix;
    private readonly Regex? _name;

    private BlockRule(string? pathPrefix, Regex? name)
    {
        _pathPrefix = pathPrefix;
        _name = name;
    }

    public static BlockRule Create(string pattern)
    {
        if (pattern.Contains('\\'))
            return new BlockRule(pattern, null);

        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return new BlockRule(null, new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    // Шлях у правилі — це або сам файл, або папка. Порівнюємо по межі папки,
    // щоб правило "C:\Games\Bad" не зачіпало сусідню "C:\Games\BadLuck".
    public bool Matches(string path, string fileName) =>
        _pathPrefix != null
            ? path.Equals(_pathPrefix.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
              path.StartsWith(_pathPrefix.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
            : _name!.IsMatch(fileName);
}
