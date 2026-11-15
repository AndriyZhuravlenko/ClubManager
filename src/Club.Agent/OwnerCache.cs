using System.Buffers.Binary;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Club.Shared;

namespace Club.Agent;

/// <summary>
/// Локальна копія даних для входу власників — на випадок, коли сервер недоступний.
/// Зберігаються лише PBKDF2-хеші (як у базі сервера), у папці, доступній тільки
/// SYSTEM і адміністраторам Windows. Список оновлює сервер при кожному підключенні агента
/// і щоразу, коли власник змінює пароль або змінюється склад власників.
/// </summary>
public class OwnerCache
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClubAgent");
    private static readonly string FilePath = Path.Combine(Folder, "owners.json");

    // Захист від перебору пароля офлайн: після 5 помилок — пауза 1 хв
    private const int MaxFailures = 5;
    private int _failures;
    private DateTime _lockedUntil = DateTime.MinValue;

    private readonly ILogger<OwnerCache> _log;
    private readonly object _lock = new();
    private List<OwnerCredential> _owners;

    public OwnerCache(ILogger<OwnerCache> log)
    {
        _log = log;
        _owners = Load();
    }

    public void Save(List<OwnerCredential> owners)
    {
        lock (_lock)
        {
            _owners = owners;
            try
            {
                EnsureProtectedFolder();
                File.WriteAllText(FilePath, JsonSerializer.Serialize(owners));
                _log.LogInformation("Оновлено дані власників для офлайн-входу: {Count}", owners.Count);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Не вдалося зберегти дані власників: {Message}", ex.Message);
            }
        }
    }

    /// <summary>Перевірити логін і пароль власника без сервера. Повертає логін або текст помилки.</summary>
    public (bool Ok, string Message) Verify(string? login, string? password)
    {
        lock (_lock)
        {
            if (DateTime.UtcNow < _lockedUntil)
                return (false, "Забагато невдалих спроб. Спробуйте за хвилину");

            var normalized = (login ?? "").Trim().ToLowerInvariant();
            var owner = _owners.FirstOrDefault(o => o.Login == normalized);
            if (owner != null && !string.IsNullOrEmpty(password) && VerifyHash(owner.PasswordHash, password))
            {
                _failures = 0;
                return (true, owner.Login);
            }

            if (++_failures >= MaxFailures)
            {
                _failures = 0;
                _lockedUntil = DateTime.UtcNow.AddMinutes(1);
            }
            return (false, "Немає зв'язку з сервером. Вхід можливий лише для власника");
        }
    }

    private List<OwnerCredential> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<OwnerCredential>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception ex)
        {
            _log.LogWarning("Не вдалося прочитати дані власників: {Message}", ex.Message);
        }
        return [];
    }

    /// <summary>Папка з доступом лише для SYSTEM і адміністраторів (без успадкування прав).</summary>
    private static void EnsureProtectedFolder()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }

        var dir = new DirectoryInfo(Folder);
        if (!dir.Exists) dir.Create(security);
        else dir.SetAccessControl(security);
    }

    /// <summary>
    /// Перевірка хешу у форматі ASP.NET Core Identity (версія 3):
    /// [0x01][PRF: 4 байти][ітерації: 4 байти][довжина солі: 4 байти][сіль][похідний ключ].
    /// </summary>
    private static bool VerifyHash(string hashBase64, string password)
    {
        try
        {
            var data = Convert.FromBase64String(hashBase64);
            if (data.Length < 13 || data[0] != 0x01) return false;

            var prf = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(1));
            var iterations = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(5));
            var saltLength = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(9));
            if (saltLength < 8 || 13 + saltLength >= data.Length) return false;

            var salt = data.AsSpan(13, saltLength);
            var expected = data.AsSpan(13 + saltLength);
            var algorithm = prf switch
            {
                0 => HashAlgorithmName.SHA1,
                1 => HashAlgorithmName.SHA256,
                2 => HashAlgorithmName.SHA512,
                _ => default
            };
            if (algorithm == default) return false;

            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, algorithm, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }
}
