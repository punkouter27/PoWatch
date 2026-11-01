using Microsoft.Extensions.Time.Testing;
using PoWatch.Shared.Services;

namespace PoWatch.Unit;

public sealed class HighlightRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 14, 0, 0, TimeSpan.Zero);

    private static PixelSample Motion(double motion) => new(T0, motion, 0.5, [], []);

    [Fact]
    public void Firsts_spikes_and_notable_captions_are_kept_within_the_hourly_budget()
    {
        var time = new FakeTimeProvider(T0);
        var rules = new HighlightRules(time, maxPerHour: 3);
        var tracker = new CentroidTracker();

        // The first cat of the session is a moment; the same cat a second later is not.
        var first = rules.OnDetections(tracker.Update(T0, [new Detection("cat", 0.9, 0.1, 0.1, 0.3, 0.3)]));
        Assert.Equal("First cat of the session", first!.Reason);
        Assert.Null(rules.OnDetections(tracker.Update(T0.AddSeconds(1), [new Detection("cat", 0.9, 0.1, 0.1, 0.3, 0.3)])));

        // A motion spike counts once per cool-down, and quiet frames never do.
        Assert.Null(rules.OnPixel(Motion(0.05)));
        Assert.Equal("Motion spike", rules.OnPixel(Motion(0.2))!.Reason);
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(rules.OnPixel(Motion(0.3)));

        // A caption the model marks notable is a moment in its own words; plain ones are not.
        Assert.Null(rules.OnCaption(new ParsedCaption("A quiet room.", [], null, Notable: false)));
        Assert.Equal("Two cats wrestle", rules.OnCaption(new ParsedCaption("Two cats wrestle", ["playing"], null, Notable: true))!.Reason);

        // The hourly budget (3) is now spent, so even a new class waits.
        Assert.Null(rules.OnDetections(tracker.Update(T0.AddSeconds(2), [new Detection("dog", 0.9, 0.5, 0.5, 0.7, 0.7)])));

        // An hour later the budget refills and a cooled-down spike is kept again.
        time.Advance(TimeSpan.FromMinutes(56));
        Assert.NotNull(rules.OnPixel(Motion(0.25)));
    }

    [Fact]
    public void A_caption_unlike_every_recent_one_is_a_moment()
    {
        var rules = new HighlightRules(new FakeTimeProvider(T0));
        static ParsedCaption Plain(string text) => new(text, [], null, Notable: false);

        // Nothing is novel until there is a full window to compare with.
        for (var i = 0; i < HighlightRules.NoveltyWindow; i++)
            Assert.Null(rules.OnCaption(Plain("A person sitting on the couch watching television.")));

        // More of the same is not a moment; something the room has not shown lately is.
        Assert.Null(rules.OnCaption(Plain("A person sitting on the couch reading.")));
        Assert.Equal("Two cats wrestle across the kitchen floor.", rules.OnCaption(Plain("Two cats wrestle across the kitchen floor."))!.Reason);

        // With embeddings, meaning decides: a paraphrase sharing no words is not new, a new scene is.
        var byMeaning = new HighlightRules(new FakeTimeProvider(T0));
        float[] couch = [1, 0, 0], paraphrase = [0.8f, 0.6f, 0], cats = [0, 0, 1];
        for (var i = 0; i < HighlightRules.NoveltyWindow; i++)
            byMeaning.OnCaption(Plain("A person sitting on the couch watching television."), couch);
        Assert.Null(byMeaning.OnCaption(Plain("Someone lounges, eyes on a screen."), paraphrase));
        Assert.NotNull(byMeaning.OnCaption(Plain("Two cats wrestle across the kitchen floor."), cats));
    }
}
