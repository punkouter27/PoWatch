using Microsoft.Extensions.Time.Testing;
using PoWatch.Application.Features.Recaps;
using PoWatch.Application.Features.Stories;
using PoWatch.Domain.Models;
using PoWatch.Domain.Services;
using PoWatch.Shared.Models;
using PoWatch.Shared.Services;

namespace PoWatch.Unit;

public sealed class PostureAndReelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 14, 0, 3, TimeSpan.Zero);

    /// <summary>17 keypoints with only the shoulders, hips and knees placed (both sides at the same point).</summary>
    private static double[] Body((double X, double Y) shoulders, (double X, double Y) hips, (double X, double Y) knees, double kneeScore = 0.9, double hipScore = 0.9)
    {
        var k = new double[PostureRules.Keypoints * PostureRules.ValuesPerKeypoint];
        void Set(int joint, (double X, double Y) at, double score)
        {
            (k[joint * 3], k[(joint * 3) + 1], k[(joint * 3) + 2]) = (at.X, at.Y, score);
        }
        foreach (var joint in new[] { 5, 6 }) Set(joint, shoulders, 0.9);
        foreach (var joint in new[] { 11, 12 }) Set(joint, hips, hipScore);
        foreach (var joint in new[] { 13, 14 }) Set(joint, knees, kneeScore);
        return k;
    }

    [Fact]
    public void Posture_is_read_from_the_trunk_and_the_thighs()
    {
        // What ViTPose+ small returned for a skier on a 640-wide COCO frame (x, y in pixels, score), in COCO order.
        double[] skier =
        [
            368, 78, 0.93, 379, 73, 1.00, 360, 73, 0.93, 393, 80, 1.00, 353, 80, 0.91, 400, 109, 0.91, 366, 115, 0.87, 424, 139, 0.87, 351, 154, 0.96,
            442, 157, 0.94, 313, 174, 0.94, 429, 204, 0.87, 405, 206, 0.83, 429, 280, 0.95, 375, 273, 0.94, 472, 358, 0.90, 406, 341, 0.87,
        ];
        var inWidths = skier.Select((v, i) => i % 3 == 2 ? v : v / 640).ToArray();
        Assert.Equal(Posture.Standing, PostureRules.Classify(inWidths));

        // Seated side-on, the thigh is level; seated facing the camera, it points at the lens. Neither drops far.
        Assert.Equal(Posture.Sitting, PostureRules.Classify(Body((0.50, 0.20), (0.50, 0.40), (0.62, 0.42))));
        Assert.Equal(Posture.Sitting, PostureRules.Classify(Body((0.50, 0.20), (0.50, 0.40), (0.50, 0.46))));
        // A level trunk is lying down, whatever the legs do.
        Assert.Equal(Posture.Lying, PostureRules.Classify(Body((0.30, 0.50), (0.55, 0.52), (0.70, 0.52))));
        // A desk camera that cannot see the knees (or the hips) says so instead of guessing.
        Assert.Equal(Posture.Unknown, PostureRules.Classify(Body((0.50, 0.20), (0.50, 0.40), (0.50, 0.46), kneeScore: 0.1)));
        Assert.Equal(Posture.Unknown, PostureRules.Classify(Body((0.50, 0.20), (0.50, 0.40), (0.50, 0.46), hipScore: 0.1)));
        Assert.Equal(Posture.Unknown, PostureRules.Classify([]));

        // One odd frame does not stand anyone up; two agreeing readings are needed to say anything.
        Assert.Equal(Posture.Sitting, PostureRules.Settled([Posture.Sitting, Posture.Standing, Posture.Sitting, Posture.Unknown]));
        Assert.Equal(Posture.Unknown, PostureRules.Settled([Posture.Sitting, Posture.Unknown]));
    }

    [Fact]
    public void A_tick_carries_the_person_seconds_spent_in_each_posture()
    {
        var time = new FakeTimeProvider(T0);
        var batcher = new TickBatcher(time);
        var tracker = new CentroidTracker();

        // Four detector passes in a window observed for 6 s (from the first pass); the first reading alone settles nothing.
        for (var second = 1; second <= 4; second++)
        {
            var at = T0.AddSeconds(second);
            var frame = tracker.Update(at, [new("person", 0.9, 0.4, 0.3, 0.6, 0.9, Posture: Posture.Sitting), new("cup", 0.9, 0.1, 0.5, 0.15, 0.55)]);
            Assert.Equal(second == 1 ? Posture.Unknown : Posture.Sitting, frame.Active.Single(t => t.Label == "person").Posture);
            batcher.AddDetections(at, frame);
        }

        // The pose model looks only every few passes: in between, the track keeps what was last read,
        // until nobody has read it for a while.
        var unread = new Detection("person", 0.9, 0.4, 0.3, 0.6, 0.9);
        Assert.Equal(Posture.Sitting, tracker.Update(T0.AddSeconds(4.5), [unread]).Active.Single().Posture);
        var later = new CentroidTracker();
        later.Update(T0, [unread with { Posture = Posture.Lying }]);
        later.Update(T0.AddSeconds(1), [unread with { Posture = Posture.Lying }]);
        for (var second = 3; second <= 21; second += 2)
            Assert.Equal(Posture.Lying, later.Update(T0.AddSeconds(second), [unread]).Active.Single().Posture);
        Assert.Equal(Posture.Unknown, later.Update(T0.AddSeconds(23), [unread]).Active.Single().Posture);

        time.Advance(TimeSpan.FromSeconds(8));
        var tick = Assert.Single(batcher.Flush()!.Ticks);
        Assert.Equal(6 * 3 / 4.0, tick.SittingSeconds, 9);
        Assert.Equal(0, tick.StandingSeconds);
        Assert.Equal(0, tick.LyingSeconds);
    }

    [Fact]
    public void Posture_seconds_merge_and_a_run_of_sat_through_minutes_is_the_longest_sit()
    {
        var session = Guid.NewGuid();
        Rollup Minute(int minute, double sitting, double standing) => Rollup.FromTick(new Tick
        {
            SessionId = session,
            StartUtc = T0.AddMinutes(minute),
            DurationSeconds = 10,
            SittingSeconds = sitting,
            StandingSeconds = standing,
        }) with
        { BucketStartUtc = T0.AddMinutes(minute) };

        // Sat through minutes 0–2, stood in 3, sat again in 4.
        Rollup[] minutes = [Minute(0, 10, 0), Minute(1, 8, 1), Minute(2, 6, 2), Minute(3, 1, 9), Minute(4, 10, 0)];
        var total = minutes.Aggregate(Rollup.Empty, Rollup.Merge);
        Assert.Equal(35, total.SittingSeconds, 9);
        Assert.Equal(12, total.StandingSeconds, 9);
        Assert.Equal(TimeSpan.FromMinutes(3), PresenceStats.Summarize(minutes, TimeSpan.FromMinutes(1)).LongestSittingStreak);

        var tooMany = new Tick { SessionId = session, StartUtc = T0, SittingSeconds = (Tick.MaxDurationSeconds * Tick.MaxPosturePeople) + 1 };
        Assert.Contains(tooMany.Validate(T0), e => e.Contains(nameof(Tick.SittingSeconds), StringComparison.Ordinal));
    }

    [Fact]
    public void A_recap_and_a_story_say_how_the_time_was_spent_only_where_posture_was_read()
    {
        var session = Guid.NewGuid();
        Tick At(int tick, double sitting, double standing) => new()
        {
            SessionId = session,
            StartUtc = T0.AddSeconds(tick * 10),
            LuminanceMean = 0.6,
            SittingSeconds = sitting,
            StandingSeconds = standing,
        };
        var names = new Dictionary<string, string>();
        var seated = Enumerable.Range(0, 12).Select(i => At(i, 9, 1)).ToList();
        Assert.Contains("Most of the time in frame was spent sitting down.", SceneNarrator.Atmosphere(seated, [], TimeZoneInfo.Utc, names), StringComparison.Ordinal);
        var mixed = Enumerable.Range(0, 12).Select(i => At(i, 5, 5)).ToList();
        Assert.Contains("split between sitting and being on foot", SceneNarrator.Atmosphere(mixed, [], TimeZoneInfo.Utc, names), StringComparison.Ordinal);
        // Unread, or too little to go on: the story says nothing about it.
        Assert.DoesNotContain("time in frame", SceneNarrator.Atmosphere([At(0, 0, 0), At(1, 9, 0)], [], TimeZoneInfo.Utc, names), StringComparison.Ordinal);

        RecapDto Recap(PresenceStatsDto presence) => TemplateRecap.Build(new RecapFacts(
            "A day", "", T0, T0.AddHours(2), TimeZoneInfo.Utc, presence, new ObjectStatsDto(), new EnvironmentStatsDto(), null, []));
        var read = Recap(new PresenceStatsDto { SecondsObserved = 7200, SittingSeconds = 5400, StandingSeconds = 600, LongestSittingSeconds = 2700 });
        Assert.Equal(["1 hour, 30 minutes", "10 minutes", "45 minutes"],
            read.Numbers.Where(n => n.Label is "Sitting" or "Standing" or "Longest sit").Select(n => n.Value));
        Assert.Contains("Longest sit: 45 minutes without a break.", read.Highlights);
        Assert.DoesNotContain(Recap(new PresenceStatsDto { SecondsObserved = 7200 }).Numbers, n => n.Label == "Sitting");
    }

    [Fact]
    public void A_long_sit_is_nudged_once_then_again_and_only_a_real_break_ends_it()
    {
        var streak = new SittingStreak(TimeSpan.FromMinutes(45));
        var nudges = new List<TimeSpan>();
        var at = T0;
        void Pass(TimeSpan length, params Posture[] people)
        {
            for (var end = at + length; at < end; at += TimeSpan.FromSeconds(10))
            {
                if (streak.Observe(at, people) is { } due) nudges.Add(due);
            }
        }

        Pass(TimeSpan.FromMinutes(30), Posture.Sitting);
        Pass(TimeSpan.FromSeconds(30), Posture.Standing);   // reaching for a shelf is not a break
        Pass(TimeSpan.FromSeconds(60));                     // nor is a minute out of the frame
        Pass(TimeSpan.FromMinutes(62), Posture.Sitting);
        Assert.Equal(2, nudges.Count);
        Assert.InRange(nudges[0].TotalMinutes, 45, 45.5);
        Assert.InRange(nudges[1].TotalMinutes, 90, 90.5);

        Pass(TimeSpan.FromSeconds(90), Posture.Standing);   // a real break
        Assert.Equal(TimeSpan.Zero, streak.Current);
        Pass(TimeSpan.FromMinutes(10), Posture.Sitting);
        Pass(TimeSpan.FromMinutes(3));                      // walked away: also a break
        Assert.Equal(TimeSpan.Zero, streak.Current);
        Assert.Equal(2, nudges.Count);

        // The nudge time is a setting: off never nudges, and a shorter one applies to the sit under way.
        streak.NudgeAfter = TimeSpan.Zero;
        Pass(TimeSpan.FromMinutes(50), Posture.Sitting);
        Assert.Equal(2, nudges.Count);
        streak.NudgeAfter = TimeSpan.FromMinutes(30);
        Pass(TimeSpan.FromSeconds(20), Posture.Sitting);
        Assert.Equal(3, nudges.Count);
    }

    [Fact]
    public void A_reel_keeps_the_liveliest_minutes_in_order_and_lingers_on_the_moments()
    {
        var day = new DateTimeOffset(2026, 9, 24, 8, 0, 0, TimeSpan.Zero);
        // Ten hours, a frame a minute; the 300th to 399th minutes are the busy ones.
        var replay = new ReplayDayDto
        {
            Minutes = Enumerable.Range(0, 600).Select(i => new ReplayMinuteDto
            {
                AtUtc = day.AddMinutes(i),
                Motion = i is >= 300 and < 400 ? 0.4 : 0.01,
                Occupancy = i is >= 300 and < 400 ? 1 : 0,
                Peak = i is >= 300 and < 400 ? 2 : 0,
            }).ToList(),
            Events =
            [
                new() { AtUtc = day.AddMinutes(10).AddSeconds(20), Kind = "moment", Text = "First cat of the day" },
                new() { AtUtc = day.AddMinutes(300), Kind = "enter", Class = "person" },
                new() { AtUtc = day.AddMinutes(301), Kind = "caption", Text = "Two people chat by the window." },
                new() { AtUtc = day.AddMinutes(302), Kind = "speech", Text = "Hello" },
            ],
        };
        var frames = Enumerable.Range(0, 600).Select(i => day.AddMinutes(i).AddSeconds(5).ToUnixTimeMilliseconds()).ToList();

        var plan = ReelPlanner.Plan(frames, replay, "Thursday 24 September 2026", TimeZoneInfo.Utc);

        Assert.Equal(ReelPlanner.Budget, plan.Shots.Sum(s => s.Hold));
        Assert.Equal(plan.Shots.Select(s => s.FrameAt).Order(), plan.Shots.Select(s => s.FrameAt));
        // The quiet morning's one moment is kept, held long, and named; every busy minute made the cut.
        var star = Assert.Single(plan.Shots, s => s.Star);
        Assert.Equal((frames[10], ReelPlanner.StarHold, "First cat of the day"), (star.FrameAt, star.Hold, star.Text));
        Assert.All(frames.Skip(300).Take(100), f => Assert.Contains(plan.Shots, s => s.FrameAt == f));
        Assert.Equal("Two people chat by the window.", plan.Shots.Single(s => s.FrameAt == frames[303]).Text);
        Assert.Null(plan.Shots.Single(s => s.FrameAt == frames[340]).Text);
        Assert.Equal(1, plan.Shots.Single(s => s.FrameAt == frames[350]).Level, 6);
        Assert.Equal(["600 minutes watched", "1 visit · 2 at once at most", "Liveliest at 13:00", "1 line said", "1 notable moment"], plan.Facts);

        // A short recording is not cut: every frame stays, held as long as a frame may be.
        var brief = ReelPlanner.Plan(frames.Take(12), replay, "A session", TimeZoneInfo.Utc);
        Assert.Equal(12, brief.Shots.Count);
        Assert.All(brief.Shots.Where(s => !s.Star), s => Assert.Equal(ReelPlanner.MaxHold, s.Hold));
        Assert.Empty(ReelPlanner.Plan([], replay, "Nothing kept", TimeZoneInfo.Utc).Shots);
    }
}
