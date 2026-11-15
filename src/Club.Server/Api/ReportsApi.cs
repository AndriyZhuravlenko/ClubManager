using System.Globalization;
using System.Text.RegularExpressions;
using Club.Server.Data;
using Club.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace Club.Server.Api;

/// <summary>
/// Звіти за період (доступні лише власнику). Усі показники обчислюються з таблиць
/// сеансів, поповнень і журналу подій. Сеанси, що частково виходять за межі періоду,
/// "обрізаються" по його межах.
/// </summary>
public static partial class ReportsApi
{
    public static void MapReportsApi(this WebApplication app)
    {
        // from/to — межі періоду (ISO, UTC); tzOffset — getTimezoneOffset() браузера, хв
        app.MapGet("/api/reports", async (DateTime from, DateTime to, int tzOffset, AppDb db, ConnectionRegistry registry) =>
        {
            from = from.ToUniversalTime();
            to = to.ToUniversalTime();
            if (to <= from) return Results.BadRequest(new { error = "Кінець періоду має бути пізніше за початок" });

            var now = DateTime.UtcNow;
            var periodEnd = to < now ? to : now;
            var periodMinutes = Math.Max(1, (periodEnd - from).TotalMinutes);
            var days = Math.Max(1.0 / 24, periodMinutes / 1440);

            var pcs = await db.Pcs.OrderBy(p => p.DisplayName).ToListAsync();
            var clients = await db.Clients.ToDictionaryAsync(c => c.Id, c => c.Login);
            var adminLogins = await db.Admins.Select(a => a.Login).ToListAsync();

            var sessions = await db.Sessions
                .Where(s => s.StartedAt < to && (s.ClosedAt == null || s.ClosedAt > from))
                .ToListAsync();

            // Тривалість кожного сеансу в межах періоду
            var clipped = sessions
                .Select(s =>
                {
                    var start = s.StartedAt > from ? s.StartedAt : from;
                    var endRaw = s.ClosedAt ?? now;
                    var end = endRaw < periodEnd ? endRaw : periodEnd;
                    return (Session: s, Start: start, End: end, Minutes: Math.Max(0, (end - start).TotalMinutes));
                })
                .Where(x => x.Minutes > 0)
                .ToList();

            string KindName(Session s) => s.ServiceAdmin != null ? "службовий" : s.ClientId != null ? "клієнтський" : "гостьовий";

            // ---- Підсумок
            var topUps = await db.TopUps.Where(t => t.At >= from && t.At < to).ToListAsync();
            var summary = new
            {
                Sessions = clipped.Count,
                Hours = Math.Round(clipped.Sum(x => x.Minutes) / 60, 1),
                ByKind = clipped.GroupBy(x => KindName(x.Session))
                    .Select(g => new { Kind = g.Key, Count = g.Count(), Hours = Math.Round(g.Sum(x => x.Minutes) / 60, 1) }),
                ChargedMinutes = sessions.Where(s => s.ClosedAt >= from && s.ClosedAt < to).Sum(s => s.ChargedMinutes),
                TopUpMinutes = topUps.Sum(t => t.Minutes),
                AvgSessionMinutes = clipped.Count > 0 ? Math.Round(clipped.Average(x => x.Minutes), 1) : 0
            };

            // ---- Завантаженість ПК
            var pcStats = pcs.Select(p =>
            {
                var mine = clipped.Where(x => x.Session.PcId == p.Id).ToList();
                var busy = mine.Sum(x => x.Minutes);
                return new
                {
                    p.DisplayName,
                    Sessions = mine.Count,
                    Hours = Math.Round(busy / 60, 1),
                    Utilization = Math.Round(busy / periodMinutes * 100, 1),
                    Agent = registry.GetMetrics(p.Id)
                };
            });

            // ---- Середня кількість зайнятих ПК за годинами доби (місцевий час)
            var byHour = new double[24];
            foreach (var x in clipped)
            {
                var t = x.Start;
                while (t < x.End)
                {
                    var local = t.AddMinutes(-tzOffset);
                    var nextHourLocal = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0).AddHours(1);
                    var next = t + (nextHourLocal - local);
                    var segmentEnd = next < x.End ? next : x.End;
                    byHour[local.Hour] += (segmentEnd - t).TotalMinutes;
                    t = segmentEnd;
                }
            }
            var hourly = byHour.Select((m, h) => new { Hour = h, AvgBusyPcs = Math.Round(m / 60 / days, 2) });

            // ---- Клієнти
            var clientStats = sessions
                .Where(s => s.ClientId != null && s.ClosedAt >= from && s.ClosedAt < to)
                .GroupBy(s => s.ClientId!.Value)
                .Select(g => new
                {
                    Login = clients.TryGetValue(g.Key, out var l) ? l : $"#{g.Key} (видалено)",
                    Sessions = g.Count(),
                    ChargedMinutes = g.Sum(s => s.ChargedMinutes)
                })
                .OrderByDescending(c => c.ChargedMinutes);

            // ---- Журнал за період: дії адміністраторів, порушення, вимірювання
            var events = await db.Events.Where(e => e.At >= from && e.At < to).ToListAsync();
            var pcNames = pcs.ToDictionary(p => p.Id, p => p.DisplayName);

            var adminStats = adminLogins.Select(login => new
            {
                Login = login,
                Actions = events.Count(e => e.Actor == login),
                TopUpMinutes = topUps.Where(t => t.Actor == login).Sum(t => t.Minutes),
                ServiceHours = Math.Round(clipped.Where(x => x.Session.ServiceAdmin == login).Sum(x => x.Minutes) / 60, 1)
            }).OrderByDescending(a => a.Actions);

            var violationEvents = events
                .Where(e => e.Actor == "агент" &&
                            (e.Text.StartsWith("Закрито недозволену програму") || e.Text.StartsWith("Закрито заборонену програму")
                             || e.Text.StartsWith("Запущено програму поза каталогом")))
                .ToList();
            var violations = new
            {
                Closed = violationEvents.Count(e => e.Text.StartsWith("Закрито")),
                Seen = violationEvents.Count(e => e.Text.StartsWith("Запущено")),
                TopPrograms = violationEvents
                    .Select(e => PathInParens().Match(e.Text) is { Success: true } m ? m.Groups[1].Value : e.Text)
                    .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new { Program = g.Key, Count = g.Count() })
                    .OrderByDescending(x => x.Count).Take(10),
                ByPc = violationEvents.GroupBy(e => e.PcId)
                    .Select(g => new { Pc = g.Key != null && pcNames.TryGetValue(g.Key.Value, out var n) ? n : "—", Count = g.Count() })
            };

            // Вимірювання з журналу: час доставки команд і відновлення зв'язку
            var commandMs = events.Select(e => CommandTime().Match(e.Text))
                .Where(m => m.Success).Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
            var reconnectSec = events.Select(e => ReconnectTime().Match(e.Text))
                .Where(m => m.Success).Select(m => double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture)).ToList();

            var measurements = new
            {
                Commands = Stats(commandMs),
                Reconnects = Stats(reconnectSec)
            };

            return Results.Ok(new { summary, pcStats, hourly, clientStats, adminStats, violations, measurements });
        }).RequireAuthorization("Owner");
    }

    private static object Stats(List<double> values) => values.Count == 0
        ? new { Count = 0, Avg = 0.0, Min = 0.0, Max = 0.0, P95 = 0.0 }
        : new
        {
            Count = values.Count,
            Avg = Math.Round(values.Average(), 1),
            Min = Math.Round(values.Min(), 1),
            Max = Math.Round(values.Max(), 1),
            P95 = Math.Round(values.OrderBy(v => v).ElementAt((int)Math.Ceiling(values.Count * 0.95) - 1), 1)
        };

    [GeneratedRegex(@"\(([^()]+)\)\s*$")]
    private static partial Regex PathInParens();

    [GeneratedRegex(@"\(за (\d+) мс\)")]
    private static partial Regex CommandTime();

    [GeneratedRegex(@"відновлено через ([\d.,]+) с")]
    private static partial Regex ReconnectTime();
}
