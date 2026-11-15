using System.Net;
using System.Text.Json.Serialization;
using Club.Server.Api;
using Club.Server.Data;
using Club.Server.Hubs;
using Club.Server.Security;
using Club.Server.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// --- Мережа ---
// HTTP — лише на самому сервері (127.0.0.1): панель адміністратора без попереджень браузера.
// HTTPS — для агентів і для доступу до панелі з інших ПК. Сертифікат створюється автоматично.
var httpPort = builder.Configuration.GetValue("HttpPort", 5000);
var httpsPort = builder.Configuration.GetValue("HttpsPort", 5001);
using var startupLogs = LoggerFactory.Create(b => b.AddConsole());
var certificate = ServerCertificate.LoadOrCreate(
    builder.Configuration["CertificatePath"] ?? "server-cert.pfx",
    startupLogs.CreateLogger("Club.Server.Certificate"));
// Тимчасовий режим для оновлення старих агентів, які ще підключаються по HTTP:
// HTTP слухається не лише на 127.0.0.1, а в усій мережі. Після оновлення агентів — вимкнути.
var allowInsecureAgents = builder.Configuration.GetValue<bool>("AllowInsecureAgents");
if (allowInsecureAgents)
    startupLogs.CreateLogger("Club.Server").LogWarning(
        "УВАГА: AllowInsecureAgents = true — агенти можуть підключатися без шифрування. Вимкніть після оновлення агентів!");
builder.WebHost.ConfigureKestrel(k =>
{
    k.Listen(allowInsecureAgents ? IPAddress.Any : IPAddress.Loopback, httpPort);
    k.Listen(IPAddress.Any, httpsPort, listen => listen.UseHttps(certificate));
});

// --- Реєстрація сервісів ---
builder.Services.AddDbContext<AppDb>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Db")));
builder.Services.AddSignalR();
builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddSingleton<AgentCommander>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<AppPolicyService>();
builder.Services.AddScoped<OwnerCredentialsService>();
builder.Services.AddHostedService<SessionWatcher>();
builder.Services.AddHostedService<MaintenanceService>();

// Автентифікація адміністраторів: захищений cookie після входу за логіном і паролем
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "club_admin";
        o.Cookie.HttpOnly = true;                       // недоступний для JavaScript
        o.Cookie.SameSite = SameSiteMode.Strict;        // захист від CSRF
        o.ExpireTimeSpan = TimeSpan.FromHours(12);      // зміна адміністратора
        o.SlidingExpiration = true;
        // Для API відповідаємо кодом 401/403, а не перенаправленням
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    });

// Авторизація за ролями (RBAC)
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Owner", p => p.RequireRole(Roles.Owner));

// Команди в API передаються текстом ("Reboot"), а не числом
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

// --- База даних ---
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    db.Database.EnsureCreated();

    // Якщо файл бази створено попередньою версією (без нових таблиць) — створюємо заново.
    // (Для навчального проєкту простіше, ніж міграції; старий журнал при цьому очищується.)
    try
    {
        _ = db.Clients.Any();
        _ = db.Sessions.Select(s => s.ClientId).Any();
        _ = db.Events.Select(e => e.Actor).Any();
    }
    catch (SqliteException)
    {
        app.Logger.LogWarning("Структура бази застаріла — створюю базу заново");
        db.Database.EnsureDeleted();
        db.Database.EnsureCreated();
    }

    // Нові таблиці етапу 4 (каталог програм, налаштування) — без втрати наявних даних
    db.AddMissingTables();

    // Перший запуск: створюємо власника admin / admin (пароль треба змінити!)
    if (!db.Admins.Any())
    {
        db.Admins.Add(new AdminUser { Login = "admin", PasswordHash = Passwords.Hash("admin"), Role = Roles.Owner, CreatedAt = DateTime.UtcNow });
        db.Log(null, "Створено обліковий запис власника admin з паролем admin — змініть пароль!");
        db.SaveChanges();
    }
}

// Сеанси, що залишилися відкритими після збою сервера, закриваємо за часом
// останнього сигналу від ПК (клієнтам не списується час простою)
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<SessionService>()
        .CloseAllAsync("Сервер було вимкнено", atLastSeen: true);
}

// Під час коректного вимкнення сервера закриваємо всі сеанси і блокуємо ПК
app.Lifetime.ApplicationStopping.Register(() =>
{
    try
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SessionService>()
            .CloseAllAsync("Сервер вимкнено", atLastSeen: false)
            .Wait(TimeSpan.FromSeconds(5));
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning("Не вдалося закрити сеанси під час вимкнення: {Message}", ex.Message);
    }
});

// Доступ до панелі з інших комп'ютерів вимикається в appsettings.json ("AllowRemoteAdmin").
// Агенти мають доступ лише до свого хабу (з ключем).
var allowRemoteAdmin = app.Configuration.GetValue<bool>("AllowRemoteAdmin");
app.Use(async (ctx, next) =>
{
    var isAgentHub = ctx.Request.Path.StartsWithSegments("/hubs/agent");
    var ip = ctx.Connection.RemoteIpAddress;
    if (!isAgentHub && !allowRemoteAdmin && (ip == null || !IPAddress.IsLoopback(ip)))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});

app.UseDefaultFiles();   // відкриває wwwroot/index.html за адресою "/"
app.UseStaticFiles(new StaticFileOptions
{
    // Браузер щоразу бере свіжу версію панелі, а не стару з кешу
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
});

app.UseAuthentication();
app.UseAuthorization();

app.MapHub<AgentHub>("/hubs/agent");
app.MapAuthApi();
app.MapAdminApi();
app.MapReportsApi();

app.Run();
