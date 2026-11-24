using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Club.Shared;
using Microsoft.Extensions.Options;

namespace Club.Agent;

/// <summary>
/// Локальний канал (named pipe) між службою агента і оболонкою користувача.
/// Служба лише ПИШЕ стан у канал, оболонка лише ЧИТАЄ.
/// Права доступу: звичайні користувачі можуть тільки читати, тому програма
/// в сеансі користувача не може надсилати службі жодних команд.
/// </summary>
public class ShellPipeServer(IOptions<AgentOptions> options, ILogger<ShellPipeServer> log) : BackgroundService
{
    private readonly List<StreamWriter> _clients = new();
    private readonly object _lock = new();
    // За замовчуванням — заблоковано; список очищення профілю оболонка отримує разом зі станом
    private ShellMessage _state = new(Locked: true, SessionEndsAt: null, Cleanup: options.Value.SessionCleanup.ToList());

    /// <summary>Змінити стан і повідомити всі підключені оболонки.</summary>
    public void SetState(bool locked, DateTime? sessionEndsAt, string? text, SessionKind kind)
    {
        lock (_lock)
        {
            // Після блокування (кінець сеансу) переписку прибираємо — наступний користувач її не побачить
            _state = _state with { Locked = locked, SessionEndsAt = sessionEndsAt, Kind = kind, Chat = locked ? null : _state.Chat };
            Broadcast(_state with { Text = text });
        }
    }

    /// <summary>Оновити каталог програм для вітрини в оболонці.</summary>
    public void SetApps(List<AppInfo> apps)
    {
        lock (_lock)
        {
            _state = _state with { Apps = apps };
            Broadcast(_state);
        }
    }

    /// <summary>Додати рядок чату з адміністратором (останні 100) і передати оболонці.</summary>
    public void AddChat(ChatLine line)
    {
        lock (_lock)
        {
            if (_state.Locked) return;
            var chat = (_state.Chat ?? new List<ChatLine>()).Append(line).TakeLast(100).ToList();
            _state = _state with { Chat = chat };
            Broadcast(_state);
        }
    }

    /// <summary>Показати користувачу повідомлення, не змінюючи стан.</summary>
    public void ShowMessage(string text)
    {
        lock (_lock) Broadcast(_state with { Text = text });
    }

    // Викликається під _lock
    private void Broadcast(ShellMessage message)
    {
        var line = JsonSerializer.Serialize(message);
        foreach (var writer in _clients.ToList())
        {
            try
            {
                writer.WriteLine(line);
                writer.Flush();
            }
            catch
            {
                // оболонку закрили — прибираємо з'єднання
                _clients.Remove(writer);
                try { writer.Dispose(); } catch { }
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var security = CreatePipeSecurity();

        while (!ct.IsCancellationRequested)
        {
            var pipe = NamedPipeServerStreamAcl.Create(
                ShellMessage.PipeName, PipeDirection.Out,
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

            var writer = new StreamWriter(pipe, new UTF8Encoding(false));
            lock (_lock)
            {
                try
                {
                    // Новій оболонці одразу передаємо поточний стан
                    writer.WriteLine(JsonSerializer.Serialize(_state));
                    writer.Flush();
                    _clients.Add(writer);
                }
                catch
                {
                    writer.Dispose();
                }
            }
            log.LogInformation("Оболонка користувача підключилася до агента");
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
            PipeAccessRights.Read, AccessControlType.Allow));
        return security;
    }
}
