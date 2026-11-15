namespace Club.Shared;

// Спільні "контракти" — класи повідомлень, якими обмінюються сервер і агент.
// Обидві сторони посилаються на цей проєкт, тому формат повідомлень завжди однаковий.

/// <summary>Назви методів SignalR-хабу (щоб не помилитися в рядках).</summary>
public static class HubMethods
{
    // Агент -> сервер
    public const string Register = "Register";
    public const string Heartbeat = "Heartbeat";
    public const string CommandResult = "CommandResult";   // підтвердження виконання команди
    public const string ClientLogin = "ClientLogin";       // клієнт входить на екрані блокування
    public const string ClientLogout = "ClientLogout";     // клієнт сам завершує сеанс
    public const string Report = "Report";                 // агент повідомляє про подію (порушення тощо)

    // Сервер -> агент
    public const string Command = "Command";
    public const string AppPolicy = "AppPolicy";           // каталог дозволених програм і режим контролю
    public const string OwnerCredentials = "OwnerCredentials"; // хеші паролів власників для офлайн-входу
}

/// <summary>Що агент повідомляє про себе при підключенні.</summary>
public record AgentHello(string MachineName, string AgentVersion);

/// <summary>Типи команд, які сервер надсилає на ПК.</summary>
public enum CommandType
{
    Lock,           // заблокувати робоче місце
    Unlock,         // розблокувати (почався сеанс)
    SessionUpdate,  // змінився час закінчення сеансу (додали час)
    ShowMessage,    // показати повідомлення користувачу
    Reboot,         // перезавантажити ПК
    Shutdown        // вимкнути ПК
}

/// <summary>
/// Повідомлення від агента (служби) до оболонки користувача через named pipe.
/// Locked — чи показувати екран блокування; SessionEndsAt — для таймера (UTC);
/// Text — повідомлення, яке треба показати користувачу (якщо є);
/// Cleanup — що видаляти з профілю користувача після сеансу (дані входу, особисті файли).
/// </summary>
public record ShellMessage(bool Locked, DateTime? SessionEndsAt, string? Text = null, List<AppInfo>? Apps = null,
    SessionKind Kind = SessionKind.None, List<string>? Cleanup = null)
{
    public const string PipeName = "ClubAgentShell";       // агент -> оболонка (стан), лише читання
    public const string LoginPipeName = "ClubAgentLogin";  // оболонка -> агент (вхід/вихід клієнта)
}

/// <summary>Програма з каталогу дозволених: назва для вітрини і повний шлях до .exe на клієнтському ПК.</summary>
public record AppInfo(int Id, string Name, string Path);

/// <summary>
/// Політика доступу до програм. Enforce = false — режим спостереження (порушення лише
/// записуються в журнал), true — недозволені програми закриваються.
/// Blocked — чорний список: ці програми закриваються завжди, в будь-якому режимі
/// (ім'я файлу, можна з * і ?, або повний шлях до файлу чи папки).
/// </summary>
public record AppPolicy(bool Enforce, List<AppInfo> Apps, List<string>? Blocked = null);

/// <summary>
/// Дані для входу власника, коли сервер недоступний: логін і PBKDF2-хеш пароля
/// (той самий, що зберігається в базі сервера). Самого пароля агент не отримує.
/// </summary>
public record OwnerCredential(string Login, string PasswordHash);

/// <summary>Запит оболонки до агента. Action: "login" або "logout".</summary>
public record ShellRequest(string Action, string? Login = null, string? Password = null);

/// <summary>Результат входу/виходу клієнта.</summary>
public record LoginResult(bool Ok, string? Error = null);

/// <summary>
/// Команда від сервера до агента.
/// SessionEndsAt — час закінчення сеансу в UTC (для таймера на екрані).
/// Kind — вид сеансу (визначає таймер, контроль програм і тексти в оболонці).
/// </summary>
public record AgentCommand(CommandType Type, string? Text = null, DateTime? SessionEndsAt = null, SessionKind Kind = SessionKind.None);

/// <summary>Вид сеансу.</summary>
public enum SessionKind
{
    None,       // сеансу немає (або ручне розблокування адміністратором)
    Guest,      // гостьовий: відкрив адміністратор на N хвилин
    Client,     // клієнтський: час списується з балансу
    Service     // службовий сеанс адміністратора: без таймера і без контролю програм
}
