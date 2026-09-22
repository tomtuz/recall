using Recall.Api;
using Recall.Api.Services;
using Xunit;

namespace Recall.Api.Tests;

public class Sm2LiteTests
{
    private static Card NewCard() => new()
    {
        Id = 1,
        TopicId = 1,
        Question = "q",
        Answer = "a",
        Difficulty = 3,
        State = CardState.New,
        Ease = 2.5,
        IntervalDays = 0,
        Reps = 0,
        Lapses = 0,
    };

    [Fact]
    public void First_good_rating_gives_one_day_interval()
    {
        var card = NewCard();
        var now = DateTime.UtcNow;

        var (state, ease, interval, due, reps, lapses) = Sm2Lite.Schedule(card, "good", now);

        Assert.Equal(CardState.Review, state);
        Assert.Equal(2.5, ease, 5);
        Assert.Equal(1, interval);
        Assert.Equal(now.AddDays(1), due, TimeSpan.FromSeconds(1));
        Assert.Equal(1, reps);
        Assert.Equal(0, lapses);
    }

    [Fact]
    public void Again_resets_to_same_day_and_counts_lapse()
    {
        var card = NewCard();
        card.State = CardState.Review;
        card.Reps = 5;
        card.IntervalDays = 30;
        card.Ease = 2.2;
        var now = DateTime.UtcNow;

        var (state, ease, interval, due, reps, lapses) = Sm2Lite.Schedule(card, "again", now);

        Assert.Equal(CardState.Relearning, state);
        Assert.Equal(0, interval);
        Assert.Equal(now, due, TimeSpan.FromSeconds(1));
        Assert.Equal(1, lapses);
        Assert.Equal(2.0, ease, 5); // 2.2 - 0.2
    }

    [Fact]
    public void Good_multiplies_interval_by_ease()
    {
        var card = NewCard();
        card.State = CardState.Review;
        card.Reps = 3;
        card.IntervalDays = 10;
        card.Ease = 2.0;
        var now = DateTime.UtcNow;

        var (_, _, interval, due, _, _) = Sm2Lite.Schedule(card, "good", now);

        Assert.Equal(20, interval);
        Assert.Equal(now.AddDays(20), due, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Easy_gives_bonus_interval_and_ease_increase()
    {
        var card = NewCard();
        card.State = CardState.Review;
        card.Reps = 2;
        card.IntervalDays = 10;
        card.Ease = 2.0;
        var now = DateTime.UtcNow;

        var (_, ease, interval, _, _, _) = Sm2Lite.Schedule(card, "easy", now);

        Assert.Equal(2.15, ease, 5); // 2.0 + 0.15
        Assert.Equal(26, interval);  // 10 * 2.0 * 1.3
    }

    [Fact]
    public void Hard_shrinks_interval_and_ease()
    {
        var card = NewCard();
        card.State = CardState.Review;
        card.Reps = 2;
        card.IntervalDays = 10;
        card.Ease = 2.0;
        var now = DateTime.UtcNow;

        var (_, ease, interval, _, _, _) = Sm2Lite.Schedule(card, "hard", now);

        Assert.Equal(1.85, ease, 5); // 2.0 - 0.15
        Assert.Equal(12, interval);  // 10 * 1.2
    }

    [Fact]
    public void Ease_is_clamped_to_bounds()
    {
        var card = NewCard();
        card.Ease = 1.35;
        var now = DateTime.UtcNow;

        var (_, ease, _, _, _, _) = Sm2Lite.Schedule(card, "again", now);
        Assert.Equal(Sm2Lite.MinEase, ease, 5);

        var card2 = NewCard();
        card2.Ease = 2.45;
        var (_, ease2, _, _, _, _) = Sm2Lite.Schedule(card2, "easy", now);
        Assert.Equal(Sm2Lite.MaxEase, ease2, 5);
    }

    [Fact]
    public void Interval_is_capped_at_180_days()
    {
        var card = NewCard();
        card.State = CardState.Review;
        card.Reps = 10;
        card.IntervalDays = 200;
        card.Ease = 2.5;
        var now = DateTime.UtcNow;

        var (_, _, interval, due, _, _) = Sm2Lite.Schedule(card, "good", now);

        Assert.Equal(Sm2Lite.MaxIntervalDays, interval);
        Assert.Equal(now.AddDays(180), due, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Unknown_rating_throws()
    {
        var card = NewCard();
        Assert.Throws<ArgumentException>(() => Sm2Lite.Schedule(card, "superb", DateTime.UtcNow));
    }
}