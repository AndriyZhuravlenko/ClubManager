using System.Collections.Concurrent;

namespace Club.Server.Security;

/// <summary>
/// Захист від підбору пароля: після 5 невдалих спроб входу під одним логіном
/// вхід під ним блокується на 1 хвилину.
/// </summary>
public class LoginThrottle
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockTime = TimeSpan.FromMinutes(1);

    private readonly record struct State(int Failures, DateTime LockedUntil);
    private readonly ConcurrentDictionary<string, State> _states = new();

    /// <summary>Текст помилки, якщо вхід зараз заблоковано, інакше null.</summary>
    public string? Check(string key)
    {
        if (_states.TryGetValue(key, out var s) && s.LockedUntil > DateTime.UtcNow)
        {
            var seconds = (int)Math.Ceiling((s.LockedUntil - DateTime.UtcNow).TotalSeconds);
            return $"Забагато невдалих спроб. Спробуйте через {seconds} с";
        }
        return null;
    }

    public void Fail(string key) =>
        _states.AddOrUpdate(key,
            _ => new State(1, DateTime.MinValue),
            (_, s) => s.Failures + 1 >= MaxFailures
                ? new State(0, DateTime.UtcNow + LockTime)
                : new State(s.Failures + 1, s.LockedUntil));

    public void Success(string key) => _states.TryRemove(key, out _);
}
