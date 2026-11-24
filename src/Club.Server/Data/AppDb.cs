using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Club.Server.Data;

/// <summary>Доступ до бази даних через Entity Framework Core.</summary>
public class AppDb(DbContextOptions<AppDb> options, Club.Server.Services.PanelNotifier? notifier = null) : DbContext(options)
{
    public DbSet<Pc> Pcs => Set<Pc>();
    public DbSet<AdminUser> Admins => Set<AdminUser>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<EventLog> Events => Set<EventLog>();
    public DbSet<AppEntry> Apps => Set<AppEntry>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<TopUp> TopUps => Set<TopUp>();
    public DbSet<BlockedApp> BlockedApps => Set<BlockedApp>();
    public DbSet<ChatMessage> Chats => Set<ChatMessage>();

    /// <summary>
    /// Створює таблиці, що з'явилися в новій версії, не чіпаючи наявних даних
    /// (замість міграцій — для навчального проєкту цього достатньо).
    /// </summary>
    public void AddMissingTables()
    {
        Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "Apps" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Apps" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT NOT NULL,
                "Path" TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS "Settings" (
                "Key" TEXT NOT NULL CONSTRAINT "PK_Settings" PRIMARY KEY,
                "Value" TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS "BlockedApps" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_BlockedApps" PRIMARY KEY AUTOINCREMENT,
                "Pattern" TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS "TopUps" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_TopUps" PRIMARY KEY AUTOINCREMENT,
                "ClientId" INTEGER NOT NULL,
                "Minutes" INTEGER NOT NULL,
                "At" TEXT NOT NULL,
                "Actor" TEXT NULL);
            CREATE TABLE IF NOT EXISTS "Chats" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Chats" PRIMARY KEY AUTOINCREMENT,
                "PcId" INTEGER NOT NULL,
                "SessionId" INTEGER NOT NULL,
                "FromAdmin" INTEGER NOT NULL,
                "Author" TEXT NOT NULL,
                "Text" TEXT NOT NULL,
                "At" TEXT NOT NULL,
                "Read" INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS "IX_Chats_SessionId" ON "Chats" ("SessionId");
            """);

        // Нові стовпці в наявній таблиці
        AddColumnIfMissing("Sessions", "ServiceAdmin", "TEXT NULL");
        AddColumnIfMissing("Sessions", "LockedAt", "TEXT NULL");
        AddColumnIfMissing("Sessions", "PausedSeconds", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("Pcs", "AgentVersion", "TEXT NULL");

        // Індекси для наявних баз (у нових їх створює EnsureCreated з тими самими назвами)
        Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS "IX_Events_At" ON "Events" ("At");
            CREATE INDEX IF NOT EXISTS "IX_Events_PcId" ON "Events" ("PcId");
            """);
    }

    // SQLite не має "ADD COLUMN IF NOT EXISTS", тому спершу перевіряємо, чи стовпець уже є
    // Назви таблиць і стовпців задає лише код сервера (не користувач), а SQL не дозволяє передати
    // назву стовпця параметром, тому підстановка в рядок тут безпечна
#pragma warning disable EF1002
    private void AddColumnIfMissing(string table, string column, string definition)
    {
        var exists = Database.SqlQueryRaw<int>(
            $"SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('{table}') WHERE name = '{column}'")
            .AsEnumerable().First() > 0;
        if (!exists)
            Database.ExecuteSqlRaw($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}");
    }
#pragma warning restore EF1002

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite не зберігає "тип" дати, тому при читанні позначаємо всі дати як UTC.
        builder.Properties<DateTime>().HaveConversion<UtcConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Pc>().HasIndex(p => p.MachineName).IsUnique();
        b.Entity<AdminUser>().HasIndex(a => a.Login).IsUnique();
        b.Entity<Client>().HasIndex(c => c.Login).IsUnique();
        b.Entity<Session>().HasIndex(s => new { s.PcId, s.ClosedAt });
        b.Entity<Setting>().HasKey(s => s.Key);
        // Індекси журналу: фільтр за ПК, очищення старих записів за датою
        b.Entity<EventLog>().HasIndex(e => e.At);
        b.Entity<EventLog>().HasIndex(e => e.PcId);
        b.Entity<ChatMessage>().HasIndex(c => c.SessionId);
    }

    // Після кожного збереження повідомляємо панелі адміністратора, які розділи змінилися
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var topics = ChangedTopics();
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        if (topics.Count > 0) notifier?.Notify(topics);
        return result;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        var topics = ChangedTopics();
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
        if (topics.Count > 0) notifier?.Notify(topics);
        return result;
    }

    private HashSet<string> ChangedTopics()
    {
        var topics = new HashSet<string>();
        foreach (var e in ChangeTracker.Entries())
        {
            if (e.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            switch (e.Entity)
            {
                case Pc: topics.Add("pcs"); break;
                case Session: topics.Add("pcs"); topics.Add("clients"); break;   // «грає / не в клубі»
                case Client or TopUp: topics.Add("clients"); break;
                case EventLog: topics.Add("events"); break;
                case AppEntry or BlockedApp or Setting: topics.Add("apps"); break;
                case AdminUser: topics.Add("admins"); break;
                case ChatMessage: topics.Add("pcs"); topics.Add("chat"); break;
            }
        }
        return topics;
    }

    /// <summary>Додати запис у журнал (зберігається разом з наступним SaveChanges).</summary>
    public void Log(int? pcId, string text, string? actor = null) =>
        Events.Add(new EventLog { At = DateTime.UtcNow, PcId = pcId, Actor = actor, Text = text });
}

public class UtcConverter() : ValueConverter<DateTime, DateTime>(
    v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
