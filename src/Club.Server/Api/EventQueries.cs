using Club.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Club.Server.Api;

/// <summary>Рядок журналу для панелі та AI-асистента.</summary>
public record EventRow(DateTime At, int? PcId, string Pc, string Actor, string Category, string Text);

/// <summary>
/// Пошук у журналі подій з фільтрами. Використовується вкладкою «Журнал» і AI-асистентом,
/// тож обидва бачать однакові результати.
/// </summary>
public static class EventQueries
{
    /// <param name="pc">"none" — події панелі без ПК; число — Id ПК; інакше — усі.</param>
    /// <param name="category">admin, client, agent, system або порожньо.</param>
    /// <param name="period">today, 24h, 7d, 30d або порожньо (увесь час).</param>
    public static async Task<List<EventRow>> RunAsync(AppDb db, string? pc, string? category, string? q, string? period, int? take)
    {
        var names = await db.Pcs.ToDictionaryAsync(p => p.Id, p => p.DisplayName);
        var adminLogins = await db.Admins.Select(a => a.Login).ToListAsync();
        var clientLogins = await db.Clients.Select(c => c.Login).ToListAsync();

        IQueryable<EventLog> query = db.Events;
        if (pc == "none") query = query.Where(e => e.PcId == null);
        else if (int.TryParse(pc, out var pcId)) query = query.Where(e => e.PcId == pcId);

        query = category switch
        {
            "admin" => query.Where(e => e.Actor != null && adminLogins.Contains(e.Actor)),
            "client" => query.Where(e => e.Actor != null && clientLogins.Contains(e.Actor)),
            "agent" => query.Where(e => e.Actor == "агент"),
            "system" => query.Where(e => e.Actor == null),
            _ => query
        };

        if (PeriodStart(period) is { } since) query = query.Where(e => e.At >= since);

        var limit = Math.Clamp(take ?? 100, 1, 1000);
        List<EventLog> events;
        if (string.IsNullOrWhiteSpace(q))
        {
            events = await query.OrderByDescending(e => e.Id).Take(limit).ToListAsync();
        }
        else
        {
            // SQLite не порівнює кирилицю без урахування регістру, тому шукаємо в пам'яті
            // серед останніх 5000 подій, що пройшли інші фільтри
            var text = q.Trim();
            events = (await query.OrderByDescending(e => e.Id).Take(5000).ToListAsync())
                .Where(e => e.Text.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                         || (e.Actor ?? "").Contains(text, StringComparison.CurrentCultureIgnoreCase))
                .Take(limit).ToList();
        }

        string CategoryOf(string? actor) =>
            actor == null ? "system"
            : actor == "агент" ? "agent"
            : adminLogins.Contains(actor) ? "admin"
            : clientLogins.Contains(actor) ? "client"
            : "other";

        return events.Select(e => new EventRow(
            e.At,
            e.PcId,
            e.PcId != null && names.TryGetValue(e.PcId.Value, out var n) ? n : "—",
            e.Actor ?? "система",
            CategoryOf(e.Actor),
            e.Text)).ToList();
    }

    /// <summary>Початок періоду в UTC. "Сьогодні" рахується від півночі за місцевим часом сервера.</summary>
    public static DateTime? PeriodStart(string? period) => period switch
    {
        "today" => DateTime.Today.ToUniversalTime(),
        "24h" => DateTime.UtcNow.AddHours(-24),
        "7d" => DateTime.UtcNow.AddDays(-7),
        "30d" => DateTime.UtcNow.AddDays(-30),
        _ => null
    };
}
