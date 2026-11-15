using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Club.Shared;

namespace Club.Shell;

/// <summary>Надсилає агенту запит на вхід/вихід клієнта через локальний канал.</summary>
public static class AgentClient
{
    public static async Task<LoginResult> SendAsync(ShellRequest request)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", ShellMessage.LoginPipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(3000);

            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

            await writer.WriteLineAsync(JsonSerializer.Serialize(request));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var line = await reader.ReadLineAsync(timeout.Token);
            return line == null
                ? new LoginResult(false, "Агент не відповів")
                : JsonSerializer.Deserialize<LoginResult>(line) ?? new LoginResult(false, "Некоректна відповідь агента");
        }
        catch (Exception)
        {
            return new LoginResult(false, "Немає зв'язку з агентом. Зверніться до адміністратора");
        }
    }
}
