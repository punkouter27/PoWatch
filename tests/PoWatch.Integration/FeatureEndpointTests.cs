using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PoWatch.Application.Contracts;
using PoWatch.Application.Services;
using PoWatch.Domain.Models;
using PoWatch.Domain.Services;
using PoWatch.Shared.Models;

namespace PoWatch.Integration;

/// <summary>Retention, weather, the weekly digest, shared recaps and the day replay, against real storage.</summary>
public sealed class FeatureEndpointTests(AzuriteWebApplicationFactory factory) : IClassFixture<AzuriteWebApplicationFactory>
{
    private static DateOnly TodayUtc => DateOnly.FromDateTime(DateTime.UtcNow);

    private HttpClient ClientFor(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fake-User", user);
        return client;
    }

    /// <summary>Starts a UTC session and lands one batch: two ticks with a motion grid, an entrance, a caption and a moment.</summary>
    private static async Task<SessionDto> ObserveAsync(HttpClient client)
    {
        var session = (await (await client.PostAsJsonAsync("/api/sessions", new StartSessionRequestDto { TimeZoneId = "UTC" }))
            .Content.ReadFromJsonAsync<SessionDto>())!;
        var start = session.StartedUtc.AddSeconds(2);
        var grid = Enumerable.Range(0, SpatialGrid.Cells).Select(i => i == 40 ? 0.8f : 0.1f).ToList();
        var response = await client.PostAsJsonAsync($"/api/sessions/{session.Id}/batches", new IngestBatchDto
        {
            BatchKey = Guid.NewGuid(),
            Ticks =
            [
                new() { StartUtc = start, PixelSamples = 40, DetectorSamples = 10, MotionMean = 0.2, MotionMax = 0.5, LuminanceMean = 0.6,
                        MotionGrid = grid, Classes = new() { ["person"] = new() { Max = 1, Mean = 1 } } },
                new() { StartUtc = start.AddSeconds(10), PixelSamples = 40, MotionMean = 0.1, MotionMax = 0.2, LuminanceMean = 0.6, MotionGrid = grid },
            ],
            Events =
            [
                new() { AtUtc = start, Kind = "TrackEnter", TrackId = "T1", Class = "person", Edge = "Left" },
                new() { AtUtc = start.AddSeconds(4), Kind = "Caption", Text = "A person crosses the room." },
                new() { AtUtc = start.AddSeconds(6), Kind = "Notable", Text = "Someone new", Score = 0.9 },
            ]
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return session;
    }

    [Fact]
    public async Task The_profile_round_trips_and_refuses_choices_it_does_not_offer()
    {
        using var client = ClientFor($"profile-{Guid.NewGuid():N}");
        var empty = await client.GetFromJsonAsync<ProfileDto>("/api/profile");
        Assert.Null(empty!.RetentionDays);
        Assert.False(empty.DigestEmailAvailable);

        var saved = await client.PutAsJsonAsync("/api/profile", new UpdateProfileDto
        {
            RetentionDays = 90,
            Place = new PlaceDto { Name = "Boston", Latitude = 42.36, Longitude = -71.06, TimeZoneId = "America/New_York" },
            TimeZoneId = "America/New_York"
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var profile = await client.GetFromJsonAsync<ProfileDto>("/api/profile");
        Assert.Equal(90, profile!.RetentionDays);
        Assert.Equal("Boston", profile.Place!.Name);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/profile", new UpdateProfileDto { RetentionDays = 12 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/profile", new UpdateProfileDto { Place = new PlaceDto { Name = "Nowhere", Latitude = 120 } })).StatusCode);
        // The Test host has no email sender, so the weekly email cannot be switched on.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/profile", new UpdateProfileDto { DigestEmail = true })).StatusCode);

        // Without a place there is no weather, and no call goes out to get it.
        using var weatherless = ClientFor($"weatherless-{Guid.NewGuid():N}");
        var noPlace = await weatherless.GetFromJsonAsync<WeatherYearDto>("/api/weather/year?tz=UTC");
        Assert.Empty(noPlace!.Days);
    }

    [Fact]
    public async Task Retention_deletes_old_raw_detail_and_keeps_the_rollups()
    {
        var user = $"retention-{Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var log = services.GetRequiredService<ISensingLog>();
        var rollups = services.GetRequiredService<IRollupStore>();
        var (oldDay, recentDay) = (TodayUtc.AddDays(-40), TodayUtc.AddDays(-1));

        Tick TickOn(DateOnly day) => new() { SessionId = Guid.NewGuid(), StartUtc = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero), PixelSamples = 4 };
        foreach (var day in new[] { oldDay, recentDay })
        {
            var tick = TickOn(day);
            await log.AppendAsync(user, day, [tick], [new SceneEvent { SessionId = tick.SessionId, AtUtc = tick.StartUtc, Kind = SceneEventKind.Caption, Text = "hello" }], CancellationToken.None);
            await rollups.MergeAsync(user, RollupGrain.Day, LocalDay.Window(day, TimeZoneInfo.Utc).StartUtc, Rollup.FromTick(tick), CancellationToken.None);
        }
        var snapshots = new BlobServiceClient(factory.StorageConnectionString).GetBlobContainerClient("snapshots");
        string Blob(DateOnly day) => $"{Uri.EscapeDataString(user)}/{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}/a.jpg";
        await snapshots.UploadBlobAsync(Blob(oldDay), BinaryData.FromString("old"));
        await snapshots.UploadBlobAsync(Blob(recentDay), BinaryData.FromString("recent"));

        var removed = await services.GetRequiredService<RetentionService>()
            .PruneAsync(new UserProfile { UserId = user, RetentionDays = 30, TimeZoneId = "UTC" }, CancellationToken.None);

        Assert.Equal(3, removed); // the old day's tick, its event and its snapshot
        Assert.Empty(await log.GetTicksAsync(user, oldDay, CancellationToken.None));
        Assert.Single(await log.GetEventsAsync(user, recentDay, CancellationToken.None));
        Assert.False((await snapshots.GetBlobClient(Blob(oldDay)).ExistsAsync()).Value);
        Assert.True((await snapshots.GetBlobClient(Blob(recentDay)).ExistsAsync()).Value);
        // Charts keep their history: both day rollups are still there.
        Assert.Equal(2, (await rollups.GetRangeAsync(user, RollupGrain.Day, LocalDay.Window(oldDay, TimeZoneInfo.Utc).StartUtc, DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None)).Count);
    }

    [Fact]
    public async Task A_stopped_session_gets_its_chapters_joined_into_one_story_written_once()
    {
        using var client = ClientFor($"storyteller-{Guid.NewGuid():N}");
        var session = await ObserveAsync(client);

        // Running and under ten minutes: no chapter has closed, so there is nothing to tell yet.
        var running = (await (await client.PostAsync($"/api/sessions/{session.Id}/story", null)).Content.ReadFromJsonAsync<SessionStoryDto>())!;
        Assert.Empty(running.Chapters);
        Assert.Null(running.Story);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/sessions/{session.Id}/stop", null)).StatusCode);
        var told = (await (await client.PostAsync($"/api/sessions/{session.Id}/story", null)).Content.ReadFromJsonAsync<SessionStoryDto>())!;
        var chapter = Assert.Single(told.Chapters);
        Assert.NotEqual(TemplateRecap.NothingObserved, chapter.Text);
        Assert.Contains(chapter.Text, told.Story, StringComparison.Ordinal);

        // Stored, not rewritten on the next read.
        var again = (await (await client.PostAsync($"/api/sessions/{session.Id}/story", null)).Content.ReadFromJsonAsync<SessionStoryDto>())!;
        Assert.Equal(told.WrittenUtc, again.WrittenUtc);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/sessions/{Guid.NewGuid()}/story", null)).StatusCode);

        // The week's story is told from the session stories of its days (Monday to Sunday).
        var week = (await client.GetFromJsonAsync<PeriodStoryDto>($"/api/stories/week?date={TodayUtc:yyyy-MM-dd}&tz=UTC"))!;
        Assert.Equal(DayOfWeek.Monday, week.From.DayOfWeek);
        Assert.Equal(TodayUtc, Assert.Single(week.Days).Date);
        Assert.Contains(chapter.Text[..20], week.Story, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/stories/year")).StatusCode);
    }

    [Fact]
    public async Task What_the_microphone_heard_is_a_transcript_and_part_of_the_story()
    {
        using var client = ClientFor($"listener-{Guid.NewGuid():N}");
        var session = (await (await client.PostAsJsonAsync("/api/sessions", new StartSessionRequestDto { TimeZoneId = "UTC" }))
            .Content.ReadFromJsonAsync<SessionDto>())!;
        var start = session.StartedUtc.AddSeconds(2);
        var ingest = await client.PostAsJsonAsync($"/api/sessions/{session.Id}/batches", new IngestBatchDto
        {
            BatchKey = Guid.NewGuid(),
            Ticks = [new() { StartUtc = start, PixelSamples = 40, LuminanceMean = 0.6, SoundMean = 0.3, SoundMax = 0.7 }],
            Events =
            [
                new() { AtUtc = start.AddSeconds(1), Kind = "Speech", Text = "Has anyone seen my keys?" },
                new() { AtUtc = start.AddSeconds(3), Kind = "Sound", Text = "Bark", Score = 0.8 },
                new() { AtUtc = start.AddSeconds(4), Kind = "Speech", Text = "Found them." },
            ]
        });
        Assert.Equal(HttpStatusCode.OK, ingest.StatusCode);

        var heard = (await client.GetFromJsonAsync<List<HeardLineDto>>($"/api/sessions/{session.Id}/transcript"))!;
        Assert.Equal(["Has anyone seen my keys?", "Found them."], heard.Select(l => l.Text));

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/sessions/{session.Id}/stop", null)).StatusCode);
        var story = (await (await client.PostAsync($"/api/sessions/{session.Id}/story", null)).Content.ReadFromJsonAsync<SessionStoryDto>())!;
        Assert.Contains("someone said: “Has anyone seen my keys?”", story.Story, StringComparison.Ordinal);
        Assert.Contains("a dog barking", story.Story, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/sessions/{Guid.NewGuid()}/transcript")).StatusCode);
    }

    [Fact]
    public async Task A_shared_recap_reads_without_signing_in_until_it_is_taken_down()
    {
        var owner = $"sharer-{Guid.NewGuid():N}";
        using var client = ClientFor(owner);
        await ObserveAsync(client);

        var created = await client.PostAsJsonAsync("/api/shares", new CreateShareDto { Day = TodayUtc, TimeZoneId = "UTC", Days = 7 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var share = (await created.Content.ReadFromJsonAsync<ShareDto>())!;
        Assert.Equal($"s/{share.Token}", share.Path);
        Assert.Contains(await client.GetFromJsonAsync<List<ShareDto>>("/api/shares") ?? [], s => s.Token == share.Token);

        using var anyone = factory.CreateClient();
        var page = await anyone.GetAsync($"/{share.Path}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("SHARED DAY RECAP", html, StringComparison.Ordinal);
        Assert.Contains("og:image", html, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", page.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("no-store", page.Headers.CacheControl!.ToString());
        Assert.Contains("noindex", page.Headers.GetValues("X-Robots-Tag").Single(), StringComparison.Ordinal);

        // QuestPDF ships no win-arm64 native library; there the card is an explained 503, as the PDF is.
        var card = await anyone.GetAsync($"/{share.Path}/card.png");
        if (card.StatusCode != HttpStatusCode.ServiceUnavailable)
        {
            Assert.Equal(HttpStatusCode.OK, card.StatusCode);
            Assert.Equal("image/png", card.Content.Headers.ContentType!.MediaType);
        }

        // Only the owner can take it down; then it is gone for everyone.
        using var stranger = ClientFor($"stranger-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"/api/shares/{share.Token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/shares/{share.Token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anyone.GetAsync($"/{share.Path}")).StatusCode);

        // Deleting everything takes the owner's links down too.
        var second = (await (await client.PostAsJsonAsync("/api/shares", new CreateShareDto { Day = TodayUtc, TimeZoneId = "UTC", Days = 1 }))
            .Content.ReadFromJsonAsync<ShareDto>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/data")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anyone.GetAsync($"/{second.Path}")).StatusCode);

        // A day with nothing in it has no recap to share, and lifetimes are only the offered ones.
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/shares", new CreateShareDto { Day = TodayUtc.AddDays(-3), TimeZoneId = "UTC", Days = 7 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/shares", new CreateShareDto { Day = TodayUtc, Days = 365 })).StatusCode);
    }

    [Fact]
    public async Task The_replay_has_each_observed_minute_with_its_grid_and_the_days_events()
    {
        using var client = ClientFor($"replay-{Guid.NewGuid():N}");
        await ObserveAsync(client);

        var replay = await client.GetFromJsonAsync<ReplayDayDto>($"/api/stats/replay?date={TodayUtc:yyyy-MM-dd}&tz=UTC");
        Assert.NotNull(replay);
        Assert.InRange(replay.Minutes.Count, 1, 2);
        var grids = Convert.FromBase64String(replay.Grids);
        Assert.Equal(replay.Minutes.Count * SpatialGrid.Cells, grids.Length);
        Assert.Equal(255, grids[40]); // the busiest cell
        Assert.Contains(replay.Events, e => e is { Kind: "caption", Text: "A person crosses the room." });
        Assert.Contains(replay.Events, e => e is { Kind: "moment" });
        Assert.Contains(replay.Events, e => e is { Kind: "enter", Class: "person" });

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/stats/replay?date=yesterday")).StatusCode);
    }

    [Fact]
    public async Task Without_an_AI_model_the_assistant_endpoints_check_input_then_say_there_is_no_model()
    {
        using var client = ClientFor($"assist-{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/ask/rule", new RuleDraftRequestDto { Text = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/ask/rule", new RuleDraftRequestDto { Text = new string('a', RuleDraftRequestDto.MaxLength + 1) })).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/ask/rule", new RuleDraftRequestDto { Text = "a dog at night" })).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/ask", new AskRequestDto { Question = "When is it busiest?" })).StatusCode);

        // The digest still answers, from the template alone.
        await ObserveAsync(client);
        var week = await client.GetFromJsonAsync<WeekDigestDto>("/api/digest/week?tz=UTC");
        Assert.False(string.IsNullOrEmpty(week!.Summary));
        Assert.Null(week.Headline);
    }

    [Fact]
    public async Task Weather_comes_from_the_chosen_place_and_the_weekly_email_goes_once()
    {
        var user = $"weekly-{Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var profiles = services.GetRequiredService<IProfileStore>();
        var rollups = services.GetRequiredService<IRollupStore>();
        var place = new Place("Testville", 10, 20, "UTC");
        await profiles.UpsertAsync(new UserProfile { UserId = user, TimeZoneId = "UTC", Place = place, DigestEmail = true, Email = "someone@example.com" }, CancellationToken.None);

        var weather = await new WeatherService(profiles, rollups, new FakeWeather(), TimeProvider.System).YearAsync(user, "UTC", CancellationToken.None);
        Assert.Equal("Testville", weather.Place!.Name);
        Assert.Equal(3, weather.Days.Count);
        Assert.Equal("Rain", weather.Days[0].Summary);
        Assert.Null(weather.Insight); // nothing observed yet

        // Monday 09:00 UTC: last week (Mon 12 Oct) had a day observed, so its digest is due.
        var monday = new FixedTime(new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero));
        var tick = new Tick { SessionId = Guid.NewGuid(), StartUtc = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero), PixelSamples = 4 };
        await rollups.MergeAsync(user, RollupGrain.Day, new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero), Rollup.FromTick(tick), CancellationToken.None);

        var mail = new FakeMail();
        var mailer = new DigestMailer(profiles, new DigestService(rollups, services.GetRequiredService<IRegularStore>()), mail, monday, NullLogger<DigestMailer>.Instance);
        await mailer.SendDueAsync(new Uri("https://powatch.example/"), CancellationToken.None);
        await mailer.SendDueAsync(new Uri("https://powatch.example/"), CancellationToken.None);

        var sent = Assert.Single(mail.Sent, m => m.To == "someone@example.com");
        Assert.Contains("12 October", sent.Subject, StringComparison.Ordinal);
        Assert.Contains("https://powatch.example/history", sent.Html, StringComparison.Ordinal);
        Assert.Equal(new DateOnly(2026, 10, 12), (await profiles.GetAsync(user, CancellationToken.None))!.LastDigestWeek);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeWeather : IWeatherSource
    {
        public Task<IReadOnlyList<DailyWeather>> GetDailyAsync(Place place, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DailyWeather>>([.. Enumerable.Range(0, 3).Select(i => new DailyWeather(to.AddDays(i - 2), 63, 12, 4, 6, null, null))]);

        public Task<IReadOnlyList<(Place Place, string? Detail)>> SearchAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<(Place, string?)>>([]);
    }

    private sealed class FakeMail : IDigestSender
    {
        public List<(string To, string Subject, string Html)> Sent { get; } = [];

        public bool IsEnabled => true;

        public Task SendAsync(string to, string subject, string html, string text, CancellationToken cancellationToken)
        {
            Sent.Add((to, subject, html));
            return Task.CompletedTask;
        }
    }
}
