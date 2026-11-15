using Microsoft.AspNetCore.Identity;

namespace Club.Server.Security;

/// <summary>
/// Хешування паролів. Використовується стандартний PasswordHasher з ASP.NET Core Identity:
/// PBKDF2 (HMAC-SHA512, 100 000 ітерацій, випадкова сіль). У базі зберігається лише хеш,
/// тому навіть з копією бази паролі дізнатися не можна.
/// </summary>
public static class Passwords
{
    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object Dummy = new();

    public static string Hash(string password) => Hasher.HashPassword(Dummy, password);

    public static bool Verify(string hash, string password) =>
        Hasher.VerifyHashedPassword(Dummy, hash, password) != PasswordVerificationResult.Failed;

    /// <summary>Мінімальні вимоги до пароля. Повертає текст помилки або null.</summary>
    public static string? Validate(string? password) =>
        string.IsNullOrWhiteSpace(password) || password.Length < 4
            ? "Пароль має містити щонайменше 4 символи"
            : null;

    /// <summary>Логіни порівнюються без урахування регістру.</summary>
    public static string NormalizeLogin(string? login) => (login ?? "").Trim().ToLowerInvariant();
}
