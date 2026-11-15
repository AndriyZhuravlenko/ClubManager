using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Club.Agent;

/// <summary>
/// Наглядач за оболонкою. Якщо в сеансі користувача, який зараз за комп'ютером,
/// немає запущеної Club.Shell (її закрили через диспетчер задач або вона впала),
/// служба запускає її знову від імені цього користувача і повідомляє адміністратора.
///
/// Служба працює в окремому сеансі 0 і не може сама показувати вікна, тому запуск
/// виконується стандартним для служб способом: токен користувача (WTSQueryUserToken)
/// + CreateProcessAsUser на робочому столі winsta0\default.
/// </summary>
public class ShellWatchdog(AgentWorker agent, ILogger<ShellWatchdog> log) : BackgroundService
{
    private static readonly string ShellPath = Path.Combine(AppContext.BaseDirectory, "Club.Shell.exe");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Перевіряємо часто: після входу у Windows оболонка має з'явитися за 2-3 секунди,
        // щоб у користувача не було часу працювати без неї
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { await CheckAsync(); }
                catch (Exception ex) { log.LogWarning("Помилка наглядача оболонки: {Message}", ex.Message); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task CheckAsync()
    {
        if (!File.Exists(ShellPath)) return;

        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) return;                        // ніхто не за комп'ютером

        var shells = Process.GetProcessesByName("Club.Shell");
        var running = shells.Any(p => p.SessionId == (int)sessionId);
        foreach (var p in shells) p.Dispose();
        if (running) return;

        if (!WTSQueryUserToken(sessionId, out var token)) return;   // користувач ще не увійшов
        try
        {
            if (StartAsUser(token))
            {
                log.LogWarning("Оболонку не знайдено в сеансі {Session} — запущено знову", sessionId);
                await agent.ReportAsync(agent.IsSessionActive
                    ? "Оболонку було закрито під час сеансу — її запущено знову"
                    : "Оболонку запущено (вхід користувача у Windows або оболонку було закрито)");
            }
            else
            {
                log.LogWarning("Не вдалося запустити оболонку: помилка Windows {Error}", Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static bool StartAsUser(IntPtr token)
    {
        CreateEnvironmentBlock(out var environment, token, false);
        try
        {
            var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            var commandLine = new StringBuilder($"\"{ShellPath}\"");
            var ok = CreateProcessAsUser(token, ShellPath, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                CreateUnicodeEnvironment, environment, AppContext.BaseDirectory, ref startup, out var info);
            if (ok)
            {
                CloseHandle(info.hProcess);
                CloseHandle(info.hThread);
            }
            return ok;
        }
        finally
        {
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
        }
    }

    // ---------- Windows API ----------

    private const uint CreateUnicodeEnvironment = 0x00000400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? applicationName, StringBuilder? commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
        IntPtr environment, string? currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
