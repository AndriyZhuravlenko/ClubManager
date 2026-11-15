using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using Club.Shared;

namespace Club.Shell;

/// <summary>
/// Оболонка користувача: працює в сеансі Windows, отримує стан від служби агента
/// через named pipe і показує екран блокування, таймер та повідомлення.
/// </summary>
public static class Program
{
    private static LockWindow _lock = null!;
    private static TimerWindow _timer = null!;

    [STAThread]
    public static void Main()
    {
        // Лише одна копія оболонки в сеансі користувача
        using var mutex = new Mutex(true, @"Local\ClubShell", out var isFirst);
        if (!isFirst) return;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        _lock = new LockWindow();
        _timer = new TimerWindow();

        // Fail-closed: поки немає зв'язку з агентом, ПК заблоковано
        _lock.ShowLocked("Немає зв'язку з агентом. Зверніться до адміністратора.");

        _ = Task.Run(ReadLoopAsync);
        app.Run();
    }

    /// <summary>Читає повідомлення від агента; при обриві — перепідключається.</summary>
    private static async Task ReadLoopAsync()
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", ShellMessage.PipeName,
                    PipeDirection.In, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000);
                using var reader = new StreamReader(pipe, Encoding.UTF8);

                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    var message = JsonSerializer.Deserialize<ShellMessage>(line);
                    if (message != null)
                        Application.Current.Dispatcher.Invoke(() => Apply(message));
                }
            }
            catch
            {
                // агент не запущений або перезапускається — спробуємо ще раз
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                _timer.Hide();
                _lock.ShowLocked("Немає зв'язку з агентом. Зверніться до адміністратора.");
            });
            await Task.Delay(2000);
        }
    }

    private static void Apply(ShellMessage m)
    {
        if (m.Apps != null) LauncherWindow.SetApps(m.Apps);

        // Якщо ПК заблоковано — текст повідомлення показуємо на екрані блокування,
        // якщо розблоковано — окремим вікном поверх гри.
        if (m.Locked)
        {
            _timer.Hide();
            LauncherWindow.HideIfOpen();
            _lock.ShowLocked(m.Text);
        }
        else
        {
            _lock.Unlock();
            _timer.Start(m.SessionEndsAt, m.Kind);
            if (!string.IsNullOrWhiteSpace(m.Text)) MessageWindow.ShowText(m.Text);
        }
    }
}
