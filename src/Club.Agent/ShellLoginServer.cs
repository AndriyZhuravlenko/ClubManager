using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Club.Shared;

namespace Club.Agent;

/// <summary>
/// Другий локальний канал: оболонка надсилає сюди запит на вхід або вихід клієнта,
/// агент пересилає його на сервер і повертає відповідь.
/// Приймаються лише запити на вхід і вихід ("login", "logout", "logout-idle") — жодних інших команд
/// від програм користувача служба не виконує.
/// </summary>
public class ShellLoginServer(AgentWorker agent, ILogger<ShellLoginServer> log) : BackgroundService
{
    private const int MaxRequestLength = 1000;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var security = CreatePipeSecurity();

        while (!ct.IsCancellationRequested)
        {
            var pipe = NamedPipeServerStreamAcl.Create(
                ShellMessage.LoginPipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                inBufferSize: 0, outBufferSize: 0, security);

            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                break;
            }

            // Кожен запит обробляємо окремо, щоб не блокувати прийом наступних
            _ = Task.Run(() => HandleAsync(pipe, ct), ct);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var pipeOwner = pipe;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

            var line = await reader.ReadLineAsync(timeout.Token);
            LoginResult result;

            if (line == null || line.Length > MaxRequestLength)
            {
                result = new LoginResult(false, "Некоректний запит");
            }
            else
            {
                var request = JsonSerializer.Deserialize<ShellRequest>(line);
                result = request?.Action switch
                {
                    "login" => await agent.ClientLoginAsync(request.Login, request.Password),
                    "logout" => await agent.ClientLogoutAsync(),
                    "logout-idle" => await agent.ClientLogoutAsync("idle"),
                    _ => new LoginResult(false, "Невідомий запит")
                };
                log.LogInformation("Запит оболонки {Action}: {Result}", request?.Action, result.Ok ? "успішно" : result.Error);
            }

            await writer.WriteLineAsync(JsonSerializer.Serialize(result));
            pipe.WaitForPipeDrain(); // чекаємо, поки оболонка прочитає відповідь
        }
        catch (Exception ex)
        {
            log.LogWarning("Помилка обробки запиту оболонки: {Message}", ex.Message);
        }
    }

    private static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }
}
