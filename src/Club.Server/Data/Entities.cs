namespace Club.Server.Data;

// Таблиці бази даних. Кожен клас = таблиця, кожна властивість = стовпець.
// Усі дати зберігаються в UTC.

/// <summary>Комп'ютер клубу (робоче місце).</summary>
public class Pc
{
    public int Id { get; set; }
    public string MachineName { get; set; } = "";   // ім'я комп'ютера в Windows
    public string DisplayName { get; set; } = "";   // назва для адміністратора, напр. "ПК-01"
    public DateTime LastSeen { get; set; }           // останній heartbeat
}

/// <summary>Ролі адміністраторів (рольова модель доступу, RBAC).</summary>
public static class Roles
{
    public const string Owner = "Owner";        // власник: усе, включно з керуванням адміністраторами
    public const string Operator = "Operator";  // адміністратор зміни: сеанси, клієнти, команди
}

/// <summary>Обліковий запис адміністратора панелі.</summary>
public class AdminUser
{
    public int Id { get; set; }
    public string Login { get; set; } = "";
    public string PasswordHash { get; set; } = "";   // PBKDF2-хеш, не сам пароль
    public string Role { get; set; } = Roles.Operator;
    public DateTime CreatedAt { get; set; }
}

/// <summary>Обліковий запис клієнта клубу.</summary>
public class Client
{
    public int Id { get; set; }
    public string Login { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public int BalanceMinutes { get; set; }          // скільки хвилин клієнт може грати
    public bool IsBlocked { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Сеанс на певному ПК (клієнтський або гостьовий, відкритий адміністратором).</summary>
public class Session
{
    public int Id { get; set; }
    public int PcId { get; set; }
    public int? ClientId { get; set; }               // null = гостьовий або службовий сеанс
    public string? ServiceAdmin { get; set; }        // логін адміністратора, якщо це службовий сеанс
    public DateTime StartedAt { get; set; }
    public DateTime EndsAt { get; set; }             // коли сеанс має закінчитися
    public DateTime? ClosedAt { get; set; }          // null = сеанс ще активний
    public string? ClosedReason { get; set; }
    public int ChargedMinutes { get; set; }          // скільки хвилин списано з балансу
    public DateTime? LockedAt { get; set; }          // не null — сеанс призупинено (адміністратор заблокував ПК)
    public int PausedSeconds { get; set; }           // загальний час призупинень (не оплачується)
    public bool WarningSent { get; set; }            // чи вже попередили "залишилось 5 хв"
}

/// <summary>Програма в каталозі дозволених.</summary>
public class AppEntry
{
    public int Id { get; set; }
    public string Name { get; set; } = "";           // назва у вітрині, напр. "Counter-Strike 2"
    public string Path { get; set; } = "";           // повний шлях до .exe на клієнтських ПК
}

/// <summary>Поповнення балансу клієнта (для звітів).</summary>
public class TopUp
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public int Minutes { get; set; }
    public DateTime At { get; set; }
    public string? Actor { get; set; }               // адміністратор, який поповнив
}

/// <summary>Налаштування системи (ключ — значення).</summary>
public class Setting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>Журнал подій (аудит): хто, що і коли зробив.</summary>
public class EventLog
{
    public int Id { get; set; }
    public DateTime At { get; set; }
    public int? PcId { get; set; }
    public string? Actor { get; set; }               // хто виконав дію (логін адміністратора/клієнта)
    public string Text { get; set; } = "";
}
