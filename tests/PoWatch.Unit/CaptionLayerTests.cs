using Microsoft.Extensions.Time.Testing;
using PoWatch.Shared.Services;

namespace PoWatch.Unit;

public sealed class CaptionLayerTests
{
    [Fact]
    public void Captions_parse_from_plain_sentences_or_json_and_junk_is_dropped()
    {
        var plain = CaptionParser.Parse("<S>A man is typing on a laptop while drinking coffee. The sun is shining.<E>");
        Assert.NotNull(plain);
        Assert.Equal("A man is typing on a laptop while drinking coffee.", plain!.Text);
        Assert.False(plain.Notable);

        var json = CaptionParser.Parse("""
            ```json
            {"caption": "Two cats wrestle on the rug", "activities": ["playing"], "weather": null, "notable": true}
            ```
            """);
        Assert.NotNull(json);
        Assert.Equal("Two cats wrestle on the rug", json!.Text);
        Assert.True(json.Notable);

        // Half-written JSON falls back to reading it as text rather than failing.
        Assert.Equal("{\"caption\": \"A dog sleeps", CaptionParser.Parse("{\"caption\": \"A dog sleeps")!.Text);

        Assert.Null(CaptionParser.Parse(null));
        Assert.Null(CaptionParser.Parse("  "));
        Assert.Null(CaptionParser.Parse("..!?"));
        Assert.True(CaptionParser.Parse(new string('a', 400))!.Text.Length <= CaptionParser.MaxLength);
    }

    [Fact]
    public void The_vlm_runs_often_when_the_scene_moves_and_rarely_when_it_is_still()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 24, 14, 0, 0, TimeSpan.Zero));
        var scheduler = new VlmScheduler(time, baseIntervalSeconds: 15);

        Assert.True(scheduler.IsDue);
        scheduler.MarkRun();
        Assert.False(scheduler.IsDue);

        // A still room drifts toward the slow end of the cadence.
        for (var i = 0; i < 50; i++) scheduler.ObserveMotion(0);
        Assert.Equal(TimeSpan.FromSeconds(AdaptiveCadence.ScoreToIntervalSeconds(0, 15)), scheduler.Interval);

        // A busy room pulls it toward the fast end.
        for (var i = 0; i < 50; i++) scheduler.ObserveMotion(0.3);
        Assert.Equal(TimeSpan.FromSeconds(AdaptiveCadence.ScoreToIntervalSeconds(1, 15)), scheduler.Interval);
        Assert.True(scheduler.Interval < TimeSpan.FromSeconds(AdaptiveCadence.ScoreToIntervalSeconds(0, 15)));

        time.Advance(scheduler.Interval);
        Assert.True(scheduler.IsDue);
    }

    [Fact]
    public void Captions_are_grounded_in_what_the_detector_sees_and_an_unchanged_scene_waits()
    {
        Assert.Equal(CaptionParser.Instruction, CaptionParser.Prompt([]));
        Assert.Equal($"{CaptionParser.Instruction}\nVisible: person x2, cat.", CaptionParser.Prompt(["cat", "person", "person"]));

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 24, 14, 0, 0, TimeSpan.Zero));
        var scheduler = new VlmScheduler(time, baseIntervalSeconds: 15);
        var couch = CaptionParser.Prompt(["person"]);
        Assert.True(scheduler.IsDueFor(couch));
        scheduler.MarkRun(couch);

        // The cadence says go, but the same scene waits; a changed one, or no detector at all, does not.
        time.Advance(scheduler.Interval);
        Assert.True(scheduler.IsDue);
        Assert.False(scheduler.IsDueFor(couch));
        Assert.True(scheduler.IsDueFor(CaptionParser.Prompt(["person", "cat"])));
        Assert.True(scheduler.IsDueFor(null));

        // Even an unchanged scene is re-captioned once its last caption is old.
        time.Advance(VlmScheduler.SameSceneEvery);
        Assert.True(scheduler.IsDueFor(couch));
    }
}
