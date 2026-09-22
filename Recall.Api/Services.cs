using System.Security.Cryptography;
using Recall.Api.Services;

namespace Recall.Api.Services;

/// <summary>
/// PBKDF2 password hashing with a per-password salt. Good enough for a coursework
/// project; ASP.NET Core's PasswordHasher does the same thing.
/// </summary>
public class PasswordHasher
{
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public bool Verify(string password, string stored)
    {
        var parts = stored.Split('.', 3);
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

/// <summary>
/// SM-2-lite: my own simplified variant of SM-2.
/// Each answer maps to the next interval; ease is clamped to [1.3, 2.5] and
/// intervals to at most 180 days. All state transitions are decided server-side.
/// </summary>
public static class Sm2Lite
{
    public const int MaxIntervalDays = 180;
    public const double MinEase = 1.3;
    public const double MaxEase = 2.5;

    /// <summary>Applies a rating to a card and returns the updated SRS values.</summary>
    public static (CardState State, double Ease, int IntervalDays, DateTime DueAt, int Reps, int Lapses) Schedule(
        Card card, string rating, DateTime nowUtc)
    {
        // Clamp stored ease at entry so out-of-range persisted values cannot propagate.
        var ease = Math.Clamp(card.Ease, MinEase, MaxEase);
        var reps = card.Reps;
        var lapses = card.Lapses;

        int interval;
        CardState state;

        switch (rating)
        {
            case "again":
                lapses += 1;
                state = CardState.Relearning;
                interval = 0; // same day
                ease = Math.Clamp(ease - 0.2, MinEase, MaxEase);
                break;
            case "hard":
                state = card.State == CardState.Learning || card.State == CardState.Relearning ? CardState.Learning : CardState.Review;
                interval = Math.Max(1, (int)Math.Round(card.IntervalDays * 1.2));
                ease = Math.Clamp(ease - 0.15, MinEase, MaxEase);
                break;
            case "good":
                state = CardState.Review; // "good" always graduates/continues review
                interval = reps == 0 ? 1 : Math.Max(1, (int)Math.Round(card.IntervalDays * ease));
                break;
            case "easy":
                state = CardState.Review;
                interval = reps == 0 ? 3 : Math.Max(1, (int)Math.Round(card.IntervalDays * ease * 1.3));
                ease = Math.Clamp(ease + 0.15, MinEase, MaxEase);
                break;
            default:
                throw new ArgumentException($"Unknown rating '{rating}'.", nameof(rating));
        }

        interval = Math.Min(interval, MaxIntervalDays);
        var dueAt = interval == 0 ? nowUtc : nowUtc.AddDays(interval);
        return (state, ease, interval, dueAt, reps + 1, lapses);
    }
}