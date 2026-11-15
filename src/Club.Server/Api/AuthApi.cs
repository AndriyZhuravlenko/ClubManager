using System.Security.Claims;
using Club.Server.Data;
using Club.Server.Security;
using Club.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace Club.Server.Api;

public record LoginRequest(string? Login, string? Password);
public record ChangePasswordRequest(string? OldPassword, string? NewPassword);
public record NewAdminRequest(string? Login, string? Password, string? Role);

/// <summary>Вхід адміністраторів у панель і керування їхніми обліковими записами.</summary>
public static class AuthApi
{
    public static void MapAuthApi(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth");

        // Вхід: перевіряємо пароль і видаємо захищений cookie з логіном і роллю
        auth.MapPost("/login", async (LoginRequest req, AppDb db, LoginThrottle throttle, HttpContext http) =>
        {
            var login = Passwords.NormalizeLogin(req.Login);
            var key = "admin:" + login;
            if (throttle.Check(key) is { } locked) return Results.BadRequest(new { error = locked });

            var admin = await db.Admins.FirstOrDefaultAsync(a => a.Login == login);
            if (admin == null || string.IsNullOrEmpty(req.Password) || !Passwords.Verify(admin.PasswordHash, req.Password))
            {
                throttle.Fail(key);
                db.Log(null, $"Невдала спроба входу в панель під логіном «{req.Login}» з {http.Connection.RemoteIpAddress}");
                await db.SaveChangesAsync();
                return Results.BadRequest(new { error = "Невірний логін або пароль" });
            }
            throttle.Success(key);

            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString()),
                new Claim(ClaimTypes.Name, admin.Login),
                new Claim(ClaimTypes.Role, admin.Role)
            ], CookieAuthenticationDefaults.AuthenticationScheme);
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

            db.Log(null, $"Вхід у панель з {http.Connection.RemoteIpAddress}", admin.Login);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        auth.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        });

        auth.MapGet("/me", (ClaimsPrincipal user) =>
            new { login = user.Identity!.Name, role = user.FindFirstValue(ClaimTypes.Role) })
            .RequireAuthorization();

        auth.MapPost("/password", async (ChangePasswordRequest req, ClaimsPrincipal user, AppDb db, OwnerCredentialsService owners) =>
        {
            var admin = await db.Admins.FindAsync(int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!));
            if (admin == null) return Results.NotFound(new { error = "Обліковий запис не знайдено" });
            if (string.IsNullOrEmpty(req.OldPassword) || !Passwords.Verify(admin.PasswordHash, req.OldPassword))
                return Results.BadRequest(new { error = "Поточний пароль невірний" });
            if (Passwords.Validate(req.NewPassword) is { } bad) return Results.BadRequest(new { error = bad });

            admin.PasswordHash = Passwords.Hash(req.NewPassword!);
            db.Log(null, "Змінено власний пароль", admin.Login);
            await db.SaveChangesAsync();
            if (admin.Role == Roles.Owner) await owners.BroadcastAsync(); // оновити дані для офлайн-входу
            return Results.Ok();
        }).RequireAuthorization();

        // --- Керування адміністраторами (тільки власник) ---
        var admins = app.MapGroup("/api/admins").RequireAuthorization("Owner");

        admins.MapGet("/", async (AppDb db) =>
            await db.Admins.OrderBy(a => a.Login)
                .Select(a => new { a.Id, a.Login, a.Role, a.CreatedAt }).ToListAsync());

        admins.MapPost("/", async (NewAdminRequest req, AppDb db, OwnerCredentialsService owners, ClaimsPrincipal user) =>
        {
            var login = Passwords.NormalizeLogin(req.Login);
            if (login.Length < 3) return Results.BadRequest(new { error = "Логін має містити щонайменше 3 символи" });
            if (Passwords.Validate(req.Password) is { } bad) return Results.BadRequest(new { error = bad });
            if (req.Role is not (Roles.Owner or Roles.Operator)) return Results.BadRequest(new { error = "Невідома роль" });
            // Логін має бути унікальним серед і адміністраторів, і клієнтів: на екрані входу вони спільні
            if (await db.Admins.AnyAsync(a => a.Login == login) || await db.Clients.AnyAsync(c => c.Login == login))
                return Results.BadRequest(new { error = "Такий логін уже зайнятий" });

            db.Admins.Add(new AdminUser { Login = login, PasswordHash = Passwords.Hash(req.Password!), Role = req.Role, CreatedAt = DateTime.UtcNow });
            var isOwner = req.Role == Roles.Owner;
            db.Log(null, $"Створено адміністратора «{login}» ({req.Role})", user.Identity!.Name);
            await db.SaveChangesAsync();
            if (isOwner) await owners.BroadcastAsync();
            return Results.Ok();
        });

        admins.MapDelete("/{id:int}", async (int id, AppDb db, OwnerCredentialsService owners, ClaimsPrincipal user) =>
        {
            if (user.FindFirstValue(ClaimTypes.NameIdentifier) == id.ToString())
                return Results.BadRequest(new { error = "Не можна видалити власний обліковий запис" });
            var admin = await db.Admins.FindAsync(id);
            if (admin == null) return Results.NotFound(new { error = "Не знайдено" });

            db.Admins.Remove(admin);
            db.Log(null, $"Видалено адміністратора «{admin.Login}»", user.Identity!.Name);
            await db.SaveChangesAsync();
            if (admin.Role == Roles.Owner) await owners.BroadcastAsync(); // видалений власник більше не ввійде офлайн
            return Results.Ok();
        });
    }
}
